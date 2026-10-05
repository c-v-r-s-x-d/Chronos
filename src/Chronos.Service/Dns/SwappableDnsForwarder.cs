namespace Chronos.Service.Dns;

/// <summary>
/// The forwarder the handler is built with, filled in by <see cref="DnsEnforcer"/> once the
/// upstream servers are known.
/// </summary>
public sealed class SwappableDnsForwarder : IDnsForwarder
{
    private volatile IDnsForwarder? _current;

    public IDnsForwarder? Current => _current;

    public void Use(IDnsForwarder? forwarder) => _current = forwarder;

    /// <summary>Null with no forwarder installed.</summary>
    public Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct) =>
        _current is { } forwarder
            ? forwarder.ForwardAsync(query, overTcp, ct)
            : Task.FromResult<byte[]?>(null);
}
