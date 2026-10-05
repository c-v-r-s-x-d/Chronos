using System.Buffers.Binary;
using Chronos.Core.Time;

namespace Chronos.Service.Dns;

/// <summary>
/// Answers held by (name, type, class) for as long as the smallest TTL in them allows. The raw
/// response bytes are kept, so a hit is an array copy plus a pass over the TTLs.
/// </summary>
public sealed class DnsResponseCache
{
    public const int DefaultCapacity = 4096;

    /// <summary>Floor and ceiling on how long anything is held, whatever the record says.</summary>
    public static readonly TimeSpan MinimumTtl = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumTtl = TimeSpan.FromHours(1);

    // Flag octets (RFC 1035 4.1.1): TC in the first, RCODE in the low nibble of the second.
    private const int FlagsOctet = 2;
    private const int RcodeOctet = 3;

    private readonly Lock _sync = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly IClock _clock;
    private readonly int _capacity;

    public DnsResponseCache(IClock clock, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _clock = clock;
        _capacity = capacity;
    }

    /// <summary>How many answers are held; a snapshot, since server threads add concurrently.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// A copy of the cached answer with this query's identifier and TTLs reduced by the time held,
    /// or null when there is none.
    /// </summary>
    public byte[]? Take(string name, ushort type, ushort qclass, ushort id)
    {
        ArgumentNullException.ThrowIfNull(name);

        var now = _clock.UtcNow;
        var key = new Key(name, type, qclass);

        byte[] answer;
        TimeSpan held;

        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (now >= entry.ExpiresAt)
            {
                // Dropped now, so a stale answer does not hold a slot.
                _entries.Remove(key);
                return null;
            }

            answer = [.. entry.Response];
            held = now - entry.StoredAt;
        }

        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(0, 2), id);

        // The wall clock can step backwards; clamp to zero rather than age by a negative amount.
        var seconds = held.Ticks / TimeSpan.TicksPerSecond;

        // Already validated by Store.
        _ = DnsTtl.TryAge(answer, seconds > 0 ? (uint)seconds : 0u);

        return answer;
    }

    /// <summary>Keeps a response if it is cacheable; otherwise does nothing.</summary>
    public void Store(string name, ushort type, ushort qclass, ReadOnlySpan<byte> response)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Only the flag octets are read by index; the walks below reject short messages themselves.
        if (response.Length <= RcodeOctet)
        {
            return;
        }

        // A truncated answer would be handed out as complete for up to an hour, including to TCP askers.
        if ((response[FlagsOctet] & DnsHeader.Truncated) != 0)
        {
            return;
        }

        // Only successful answers. The RCODE is twelve bits: the header holds the low four and an
        // OPT record the rest (RFC 6891 6.1.3), so the header alone would read codes from 16 up as success.
        if ((response[RcodeOctet] & DnsHeader.RcodeMask) != DnsHeader.RcodeNoError ||
            DnsTtl.ExtendedRcode(response) != 0)
        {
            return;
        }

        // Null means nothing expires; zero means do not cache (RFC 1035 4.1.3).
        if (DnsTtl.Smallest(response) is not { } smallest || smallest == 0)
        {
            return;
        }

        var now = _clock.UtcNow;
        var lifetime = TimeSpan.FromTicks(
            Math.Clamp(TimeSpan.FromSeconds(smallest).Ticks, MinimumTtl.Ticks, MaximumTtl.Ticks));

        var key = new Key(name, type, qclass);
        var entry = new Entry([.. response], now, now + lifetime);

        lock (_sync)
        {
            // A key already held is replaced, and replacing costs no room.
            if (_entries.Count >= _capacity && !_entries.ContainsKey(key))
            {
                DropSoonestToExpire();
            }

            _entries[key] = entry;
        }
    }

    /// <summary>
    /// Drops the answer with the least time left; the caller holds the lock and the cache is full.
    /// A linear scan: microseconds at the default capacity, and LRU would need a write on every hit.
    /// </summary>
    private void DropSoonestToExpire() => _entries.Remove(_entries.MinBy(entry => entry.Value.ExpiresAt).Key);

    /// <summary>One question. The name is compared Ordinal; DnsName already lower-cases it.</summary>
    private readonly record struct Key(string Name, ushort Type, ushort Class);

    /// <summary>One held answer. <see cref="StoredAt"/> is kept because the TTL clamp makes it not derivable from <see cref="ExpiresAt"/>.</summary>
    private readonly record struct Entry(byte[] Response, DateTimeOffset StoredAt, DateTimeOffset ExpiresAt);
}
