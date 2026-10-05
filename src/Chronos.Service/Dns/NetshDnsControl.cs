using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

public interface INetworkDnsControl
{
    /// <summary>Puts this list on the interface, in this order, as a static configuration.</summary>
    bool SetStatic(int interfaceIndex, IReadOnlyList<string> servers);

    /// <summary>Puts the interface back to taking its resolvers from DHCP.</summary>
    bool RestoreDhcp(int interfaceIndex);
}

/// <summary>The netsh command lines, separate from running them so tests can pin them.</summary>
/// <remarks>
/// Interfaces are named by index, since names are localized. Only DNS servers are set: anything
/// else (registration, suffix, IPv6 resolvers) is not backed up and so must not be touched.
/// </remarks>
internal static class NetshCommands
{
    /// <summary>The first server, replacing whatever list the interface carried.</summary>
    public static string SetFirst(int interfaceIndex, string server)
    {
        CheckInterface(interfaceIndex);
        CheckServer(server);

        // validate=no: the resolver may not be listening yet, and a validating netsh waits seconds per interface.
        return $"interface ipv4 set dnsservers name={interfaceIndex} source=static "
            + $"address={server} validate=no";
    }

    /// <summary>A further server, at its place in the list.</summary>
    public static string AddNext(int interfaceIndex, string server, int position)
    {
        CheckInterface(interfaceIndex);
        CheckServer(server);

        // Position 1 belongs to SetFirst and AddFirst; taking it would push 127.0.0.1 down the list.
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 2);

        return $"interface ipv4 add dnsservers name={interfaceIndex} address={server} index={position} validate=no";
    }

    /// <summary>The resolver, put in front of a list that already holds an outside server. Run last, so a failed step never leaves 127.0.0.1 alone.</summary>
    public static string AddFirst(int interfaceIndex, string server)
    {
        CheckInterface(interfaceIndex);
        CheckServer(server);

        // Only this machine goes in front: an outside server there would push the resolver down.
        if (!IPAddress.IsLoopback(IPAddress.Parse(server)))
        {
            throw new ArgumentException($"'{server}' is not an address of this machine.", nameof(server));
        }

        return $"interface ipv4 add dnsservers name={interfaceIndex} address={server} index=1 validate=no";
    }

    /// <summary>Hands the interface back to DHCP, which drops the static list with it.</summary>
    public static string Dhcp(int interfaceIndex)
    {
        CheckInterface(interfaceIndex);

        // No validate=no here: source=dhcp names no server to validate.
        return $"interface ipv4 set dnsservers name={interfaceIndex} source=dhcp";
    }

    private static void CheckInterface(int interfaceIndex) =>
        ArgumentOutOfRangeException.ThrowIfLessThan(interfaceIndex, 1, nameof(interfaceIndex));

    /// <summary>Accepts an IPv4 address only: the command is split on spaces, and the values come from the registry.</summary>
    private static void CheckServer(string server)
    {
        // Other unusable strings are refused by the parse below.
        ArgumentNullException.ThrowIfNull(server);

        if (!IPAddress.TryParse(server, out var address)
            || address.AddressFamily is not AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                $"'{server}' is not an IPv4 address, and 'interface ipv4 ... dnsservers' takes nothing else.",
                nameof(server));
        }
    }
}

/// <summary>
/// Writes DNS settings through netsh.exe; reading is <see cref="InterfaceDnsRegistry"/>'s. Writing
/// the registry directly would leave the DNS client holding the old values.
/// </summary>
/// <remarks>Only the exit code is read. The output is localized and goes to the Debug log.</remarks>
[SupportedOSPlatform("windows")]
public sealed class NetshDnsControl : INetworkDnsControl
{
    /// <summary>Finite because this runs on the service's start and stop paths.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Exit code reported for a call that never answered; netsh's own codes are not negative.</summary>
    internal const int NoAnswer = -1;

    /// <summary>The full path, not the name: this runs as LocalSystem, and PATH must not decide what "netsh" is.</summary>
    internal static string NetshPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

    private readonly ILogger<NetshDnsControl> _logger;
    private readonly Func<string, NetshResult> _run;

    public NetshDnsControl(ILogger<NetshDnsControl> logger)
        : this(logger, Run)
    {
    }

    /// <summary>The same class with the call onto the machine replaced, for tests.</summary>
    internal NetshDnsControl(ILogger<NetshDnsControl> logger, Func<string, NetshResult> run)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(run);

        _logger = logger;
        _run = run;
    }

    public bool SetStatic(int interfaceIndex, IReadOnlyList<string> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        if (servers.Count == 0)
        {
            // netsh would leave the interface with no resolver at all.
            throw new ArgumentException(
                "A static DNS list has to name at least one server.", nameof(servers));
        }

        // All commands are built before the first runs, so a bad address cannot leave the interface half taken over.
        var commands = new List<string>();

        // The outside part of the list goes first and the leading loopback addresses last, so any
        // failed step leaves an outside server on the interface.
        var leading = servers.TakeWhile(IsLoopback).Count();

        for (var at = leading; at < servers.Count; at++)
        {
            commands.Add(at == leading
                ? NetshCommands.SetFirst(interfaceIndex, servers[at])
                : NetshCommands.AddNext(interfaceIndex, servers[at], at - leading + 1));
        }

        for (var at = 0; at < leading; at++)
        {
            commands.Add(at == 0
                ? NetshCommands.AddFirst(interfaceIndex, servers[at])
                : NetshCommands.AddNext(interfaceIndex, servers[at], at + 1));
        }

        if (!servers.Any(IsExternal))
        {
            // A list of only loopback addresses would leave nothing resolving once this service stops.
            // A DHCP interface handed no servers produces exactly this. Checked after the commands are
            // built so a malformed address is reported as that.
            throw new ArgumentException(
                "A static DNS list has to keep a server outside this machine.",
                nameof(servers));
        }

        foreach (var command in commands)
        {
            if (!Execute(command))
            {
                // Nothing is undone here; the restore path holds the backup.
                return false;
            }
        }

        return true;
    }

    public bool RestoreDhcp(int interfaceIndex) => Execute(NetshCommands.Dhcp(interfaceIndex));

    private static bool IsExternal(string server) =>
        IPAddress.TryParse(server, out var address) && !IPAddress.IsLoopback(address);

    private static bool IsLoopback(string server) =>
        IPAddress.TryParse(server, out var address) && IPAddress.IsLoopback(address);

    private bool Execute(string command)
    {
        var result = _run(command);

        // Output is localized: Debug only, never decided on.
        _logger.LogDebug(
            "netsh {Command} exited with {ExitCode} and said: {Output}",
            command, result.ExitCode, result.Output);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("netsh {Command} failed with exit code {ExitCode}.", command, result.ExitCode);

            return false;
        }

        return true;
    }

    private static NetshResult Run(string command) => Run(StartOf(command), CallTimeout);

    /// <summary>The netsh start info for one command, built but not started, so tests can inspect it.</summary>
    internal static ProcessStartInfo StartOf(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var info = new ProcessStartInfo(NetshPath)
        {
            // No shell, no window (this runs in a service). Both streams are read: netsh writes some
            // refusals to stderr.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // No argument contains a space (addresses are validated), so each word is one argument and no quoting is involved.
        foreach (var argument in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>
    /// Runs a prepared start and returns its exit code, or kills it and returns <see cref="NoAnswer"/>
    /// after <paramref name="timeout"/>.
    /// </summary>
    internal static NetshResult Run(ProcessStartInfo start, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(start);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{start.FileName}' could not be started.");

        // Read before the wait: a child whose pipe fills up stops until somebody drains it.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            Kill(process);

            return new NetshResult(
                NoAnswer,
                $"{Path.GetFileName(start.FileName)} did not answer within "
                    + $"{timeout.TotalSeconds:0} seconds and was stopped.");
        }

        // The last output may still be in the pipe; missing it only costs a log line.
        var said = Task.WhenAll(output, error).Wait(DrainTimeout)
            ? string.Concat(output.Result, error.Result)
            : string.Empty;

        return new NetshResult(process.ExitCode, said);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited between the wait giving up and the kill.
        }
    }

    internal readonly record struct NetshResult(int ExitCode, string Output);
}
