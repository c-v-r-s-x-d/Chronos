using Chronos.Service.Dns;

namespace Chronos.Service.Tests;

/// <summary>The interfaces of a machine that exists only in the test, with the lists netsh would have left in the registry.</summary>
internal sealed class FakeInterfaceDns : IInterfaceDns
{
    private readonly List<InterfaceDnsState> _states = [];

    // What DHCP hands each interface, so that a restore to DHCP reads back as the network gave it.
    private readonly Dictionary<string, IReadOnlyList<string>> _handed = new(StringComparer.Ordinal);

    public int Reads { get; private set; }

    public IReadOnlyList<InterfaceDnsState> Current => [.. _states];

    public FakeInterfaceDns Add(string guid, int index, bool isDhcp, params string[] servers)
    {
        _states.Add(new InterfaceDnsState(guid, index, $"Adapter {index}", isDhcp, servers));

        if (isDhcp)
        {
            _handed[guid] = servers;
        }

        return this;
    }

    public void Remove(string guid) => _states.RemoveAll(state => state.Guid == guid);

    /// <summary>What DHCP hands the interface now, as after a move to another network.</summary>
    public void Hand(string guid, params string[] servers) => _handed[guid] = servers;

    public IReadOnlyList<string> Handed(string guid) => _handed.TryGetValue(guid, out var handed) ? handed : [];

    public InterfaceDnsState Of(int index) => _states.Single(state => state.Index == index);

    public void SetStatic(int index, IReadOnlyList<string> servers) =>
        Replace(index, state => state with { IsDhcp = false, Servers = [.. servers] });

    public void SetDhcp(int index) =>
        Replace(index, state => state with
        {
            IsDhcp = true,
            Servers = _handed.TryGetValue(state.Guid, out var handed) ? handed : [],
        });

    /// <summary>Called as a pass reads the interfaces, for tests about what changes in between.</summary>
    public Action? BeforeRead { get; set; }

    public IReadOnlyList<InterfaceDnsState> Read()
    {
        Reads++;
        BeforeRead?.Invoke();

        return Current;
    }

    /// <summary>Adapters on the machine that carry no traffic: named by index, never read.</summary>
    public Dictionary<string, int> Inactive { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> Indexes()
    {
        var indexes = new Dictionary<string, int>(Inactive, StringComparer.OrdinalIgnoreCase);

        foreach (var state in _states)
        {
            indexes[state.Guid] = state.Index;
        }

        return indexes;
    }

    private void Replace(int index, Func<InterfaceDnsState, InterfaceDnsState> change)
    {
        var position = _states.FindIndex(state => state.Index == index);
        if (position >= 0)
        {
            _states[position] = change(_states[position]);
        }
    }
}

/// <summary>netsh, as the calls it was asked to make, applied to <see cref="FakeInterfaceDns"/> when it takes them.</summary>
internal sealed class FakeDnsControl(FakeInterfaceDns? machine = null, List<string>? events = null) : INetworkDnsControl
{
    /// <summary>Every call in order; a null list is a return to DHCP.</summary>
    public List<(int Index, IReadOnlyList<string>? Servers)> Calls { get; } = [];

    /// <summary>Interfaces on which netsh exits non-zero.</summary>
    public HashSet<int> Refuse { get; } = [];

    /// <summary>Interfaces on which netsh cannot even be started.</summary>
    public HashSet<int> Throw { get; } = [];

    /// <summary>Called before each call is taken, for tests about what was true at that moment.</summary>
    public Action<int>? Before { get; set; }

    public bool SetStatic(int interfaceIndex, IReadOnlyList<string> servers)
    {
        Before?.Invoke(interfaceIndex);
        Calls.Add((interfaceIndex, [.. servers]));
        events?.Add($"netsh:{interfaceIndex}");

        if (!Takes(interfaceIndex))
        {
            return false;
        }

        machine?.SetStatic(interfaceIndex, servers);
        return true;
    }

    public bool RestoreDhcp(int interfaceIndex)
    {
        Before?.Invoke(interfaceIndex);
        Calls.Add((interfaceIndex, null));
        events?.Add($"netsh:{interfaceIndex}");

        if (!Takes(interfaceIndex))
        {
            return false;
        }

        machine?.SetDhcp(interfaceIndex);
        return true;
    }

    private bool Takes(int interfaceIndex)
    {
        if (Throw.Contains(interfaceIndex))
        {
            throw new System.ComponentModel.Win32Exception(2);
        }

        return !Refuse.Contains(interfaceIndex);
    }
}

/// <summary>The registry half of the backup, in memory.</summary>
internal sealed class MemoryMirror(List<string>? events = null) : IBackupMirror
{
    public string? Held { get; set; }

    public int Writes { get; private set; }

    public int Reads { get; private set; }

    /// <summary>A locked-down key: every call is refused.</summary>
    public bool Refuses { get; set; }

    public void Write(string json)
    {
        Refuse();
        Writes++;
        events?.Add("backup");
        Held = json;
    }

    public string? Read()
    {
        Reads++;
        Refuse();

        return Held;
    }

    /// <summary>Called as the copy is taken away, for tests about what holds at that moment.</summary>
    public Action? BeforeClear { get; set; }

    public void Clear()
    {
        BeforeClear?.Invoke();
        Refuse();
        Held = null;
    }

    private void Refuse()
    {
        if (Refuses)
        {
            throw new UnauthorizedAccessException();
        }
    }
}

/// <summary>The settings lock, held by "the other side" when the test says so.</summary>
internal sealed class FakeDnsSettingsLock : IDnsSettingsLock
{
    /// <summary>The other process holds it: every attempt fails after its wait.</summary>
    public bool HeldElsewhere { get; set; }

    /// <summary>Every wait asked for, in order.</summary>
    public List<TimeSpan> Waits { get; } = [];

    /// <summary>Whether this side holds it now.</summary>
    public bool Held { get; private set; }

    public IDisposable? TryAcquire(TimeSpan wait)
    {
        Waits.Add(wait);

        if (HeldElsewhere)
        {
            return null;
        }

        Held = true;

        return new Release(this);
    }

    private sealed class Release(FakeDnsSettingsLock owner) : IDisposable
    {
        public void Dispose()
        {
            owner.Held = false;
        }
    }
}
