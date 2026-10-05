using Chronos.Ipc;
using Serilog;
using Serilog.Core;

namespace Chronos.App.Diagnostics;

/// <summary>
/// Everything the interface logs, and the only place it words it. Always English, whatever the
/// screen's language. Records answer "why doesn't it work"; there is no record of sites visited,
/// applications started or sessions, and no full path above Debug.
/// </summary>
public sealed class AppLog : IDisposable
{
    private readonly ILogger _sink;

    public AppLog(ILogger sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
    }

    /// <summary>A log that keeps nothing; the default everywhere, so tests need no file.</summary>
    public static AppLog Silent { get; } = new(Logger.None);

    public void Started() => _sink.Information("The Chronos interface started.");

    public void Stopped() => _sink.Information("The Chronos interface closed.");

    /// <summary>The channel is there. Written on the change only - see <c>ServiceLink.Record</c>.</summary>
    public void ServiceReached() =>
        _sink.Information("The service answered; the interface is following its status.");

    public void ServiceLost() =>
        _sink.Warning("The service cannot be reached; the interface is showing it as unavailable.");

    /// <summary>Reached and refused: the service is healthy but the two halves came from different builds.</summary>
    public void ServiceSpeaksAnotherProtocol() =>
        _sink.Warning(
            "The service refused the subscription; it does not speak protocol version {Version}.",
            IpcProtocol.Version);

    /// <summary>Reached and accepted, with a status that parsed but lost something. Not a version mismatch.</summary>
    public void ServiceSentAnUnusableAcknowledgement() =>
        _sink.Warning(
            "The service accepted the subscription, but its status could not be used; the service is shown as unavailable.");

    /// <summary>A command that got no answer. The fault is named by type, not message: this is at <c>Warning</c>, where a message could carry a path.</summary>
    public void CommandUnanswered(IpcRequest request, Exception fault)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fault);

        if (Named(request) is { } subject)
        {
            _sink.Warning(
                "The service could not be reached for {Command} of {Subject} ({Fault}).",
                request.Command,
                subject,
                fault.GetType().Name);

            return;
        }

        _sink.Warning(
            "The service could not be reached for {Command} ({Fault}).",
            request.Command,
            fault.GetType().Name);
    }

    /// <summary>A command the service answered and would not carry out.</summary>
    public void CommandRefused(IpcRequest request, IpcResponse answer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(answer);

        // The code as it came: English, and it quotes nothing from the request, so no path reaches the log.
        var reason = answer.Error ?? "no reason given";

        if (Named(request) is { } subject)
        {
            _sink.Warning(
                "The service refused {Command} of {Subject}: {Reason}",
                request.Command,
                subject,
                reason);

            return;
        }

        _sink.Warning("The service refused {Command}: {Reason}", request.Command, reason);
    }

    /// <param name="from">The language left, as a code.</param>
    /// <param name="to">The language chosen, as a code and never as its own name for itself.</param>
    public void LanguageChanged(string from, string to) =>
        _sink.Information("The interface language changed from {From} to {To}.", from, to);

    public void AutostartRefused() =>
        _sink.Warning("The logon entry could not be written; the interface will not start at the next logon.");

    /// <summary>An exception nobody caught, with the exception itself for the stack.</summary>
    public void Unhandled(string where, Exception error) =>
        _sink.Error(error, "An unhandled exception reached {Where}.", where);

    public void Dispose() => (_sink as IDisposable)?.Dispose();

    /// <summary>What the command was about, in the form allowed above <c>Debug</c>, or null. A domain or executable name from the user's own list may be written; a full path may not.</summary>
    private static string? Named(IpcRequest request)
    {
        if (request.Site is { Domain.Length: > 0 } site)
        {
            return site.Domain;
        }

        if (request.App is not { Value.Length: > 0 } app)
        {
            return null;
        }

        return Path.GetFileName(app.Value) is { Length: > 0 } name ? name : null;
    }
}
