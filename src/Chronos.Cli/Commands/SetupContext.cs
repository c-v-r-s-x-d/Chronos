using System.Runtime.Versioning;
using System.Text.Json;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Chronos.Service.Dns;
using Chronos.Service.Setup;
using Chronos.Service.Sites;
using Chronos.Service.State;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Cli.Commands;

/// <summary>Whether the resolver could take 127.0.0.1 on a port (UDP and TCP), and who holds it if not. <see cref="Holder"/> is null when it could not be named.</summary>
public readonly record struct LoopbackPort(bool Free, string? Holder);

/// <summary>The machine-touching parts of cleanup and <c>diag</c>, replaceable in tests.</summary>
internal sealed record MachineParts(
    string HostsPath,
    Func<IWfpEngine> OpenFilterEngine,
    Func<DnsBackupStore> OpenDnsBackups,
    Func<DnsBackupStore, DnsRestore> OpenDnsRestore,
    IDnsSettingsLock DnsLock,
    Func<ProductKeyRemoval> RemoveProductKey)
{
    [SupportedOSPlatform("windows")]
    public static MachineParts Real(ChronosPaths paths) => new(
        HostsFile.SystemPath,
        CleanCommand.OpenFilterEngine,
        () => CleanCommand.OpenDnsBackups(paths),
        CleanCommand.OpenDnsRestore,
        new NamedDnsSettingsLock(),
        ProductKey.RemoveIfEmpty);
}

/// <summary>Everything <c>install</c>, <c>uninstall</c>, <c>recover</c> and <c>diag</c> do to or read from a machine, as collaborators tests can replace.</summary>
/// <remarks>
/// <see cref="CommandPath"/> is the CLI, which the recovery task runs at boot; the service manager
/// gets the service executable from <see cref="Definition"/>. The last members are read-only and used by <c>diag</c>.
/// </remarks>
public sealed record SetupContext(
    IServiceRegistration Services,
    Func<CancellationToken, Task<IpcResponse>> ServiceStatus,
    ITaskRegistration Tasks,
    ISystemEventLog EventLog,
    IAutostartEntry Autostart,
    ChronosPaths Paths,
    ServiceDefinition Definition,
    string CommandPath,
    Func<TextWriter, TextWriter, Task<CleanResult>> Clean,
    Action ClearState,
    Action<string> CreateDataDirectory,
    Func<IWfpEngine> OpenFilterEngine,
    Func<IReadOnlyList<string>?> ReadHostsBlock,
    Func<int, PortUse> OwnerOfPort,
    Func<IReadOnlyList<InterfaceDns>> DnsSettings,
    Func<int, LoopbackPort> ProbeLoopbackPort,
    Func<DnsBackupSources> DnsBackups,
    Func<TextWriter, TextWriter, CleanResult> RestoreDns,
    Func<ProductKeyRemoval> RemoveProductKey)
{
    private const string ServiceExecutable = "Chronos.Service.exe";

    /// <summary>How long the service has to answer.</summary>
    /// <remarks>
    /// <see cref="IpcClient"/> bounds only the connect. A service with a jammed dispatcher still
    /// accepts connections and never replies, which would hang <c>recover</c> until the scheduler kills it.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    [SupportedOSPlatform("windows")]
    public static SetupContext ForThisMachine() =>
        ForThisMachine(ChronosPaths.Default, IpcProtocol.DefaultPipeName, ProbeTimeout);

    /// <summary>The same wiring with the state path, pipe name and answer timeout supplied, so tests can exercise the probe and <c>ClearState</c>.</summary>
    [SupportedOSPlatform("windows")]
    internal static SetupContext ForThisMachine(ChronosPaths paths, string pipeName, TimeSpan probeTimeout) =>
        ForThisMachine(paths, pipeName, probeTimeout, MachineParts.Real(paths));

    /// <summary>The same wiring over supplied machine parts.</summary>
    [SupportedOSPlatform("windows")]
    internal static SetupContext ForThisMachine(
        ChronosPaths paths, string pipeName, TimeSpan probeTimeout, MachineParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        // The installer copies the whole product into one directory and runs this tool from it.
        var command = Environment.ProcessPath
            ?? throw new InvalidOperationException("Chronos could not tell where it is running from.");

        var directory = Path.GetDirectoryName(command)
            ?? throw new InvalidOperationException($"'{command}' has no directory to install from.");

        DnsRestore OpenDnsRestore() => parts.OpenDnsRestore(parts.OpenDnsBackups());

        return new SetupContext(
            new WindowsServiceRegistration(),
            ct => AskAsync(pipeName, probeTimeout, ct),
            new SchtasksRegistration(),
            new WindowsEventLog(),
            new UserAutostart(),
            paths,
            ServiceDefinition.Chronos(Path.Combine(directory, ServiceExecutable)),
            command,
            (output, error) => CleanCommand.CleanAsync(
                new HostsFile(parts.HostsPath), parts.OpenFilterEngine, OpenDnsRestore, parts.DnsLock, output, error),
            // No logger: Clear raises on failure and the caller reports it.
            () => new StateStore(paths, NullLogger<StateStore>.Instance).Clear(),
            DataDirectory.Create,
            parts.OpenFilterEngine,
            () => new HostsFile(parts.HostsPath).ReadBlock(),
            PortOwner.Find,
            NetworkDns.Active,
            // Binds and releases at once; the resolver is not started.
            port => new LoopbackPort(LoopbackDnsServer.CanTake(port, out var holder), holder),
            () => parts.OpenDnsBackups().Sources(),
            (output, error) => CleanCommand.RestoreDns(OpenDnsRestore, parts.DnsLock, output, error, serviceStopped: true),
            parts.RemoveProductKey);
    }

    /// <summary>Whether the service answers on its pipe. Any reply counts, a refusal included.</summary>
    public async Task<bool> AnswersAsync(CancellationToken ct)
    {
        try
        {
            await ServiceStatus(ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception)
            when (exception is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable reply counts as no answer; an unhandled JsonException here would leave
            // the machine blocked. Cancellation is not caught: the caller asked to stop.
            return false;
        }
    }

    /// <summary>Asks <c>GetStatus</c>, which reads nothing from disk, so a service with a broken configuration still answers.</summary>
    private static async Task<IpcResponse> AskAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);

        try
        {
            return await new IpcClient(pipeName)
                .SendAsync(new IpcRequest { Command = "GetStatus" }, bounded.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A timeout, not a cancellation: callers treat the two differently.
            throw new TimeoutException(
                $"The Chronos service took the connection on '{pipeName}' and did not answer within {timeout}.");
        }
    }
}
