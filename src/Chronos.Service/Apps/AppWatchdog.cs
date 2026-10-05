using Chronos.Core.Rules;
using Chronos.Core.Time;
using Chronos.Ipc;
using Chronos.Service.Diagnostics;
using Chronos.Service.Ipc;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Apps;

/// <summary>
/// Decides whether a process must end. <see cref="OnProcessStarted"/> runs on the event pump thread
/// and <see cref="Arm"/> on the reconcile loop, so all state is held under one lock.
/// </summary>
public sealed class AppWatchdog(
    IProcessControl processes,
    IProtectedAppPolicy protection,
    IClock clock,
    EventBus events,
    // A delegate, not the engine: SessionEngine is not thread-safe. The delegate takes the session gate itself.
    Func<StatusPayload> status,
    ILogger<AppWatchdog> logger)
{
    /// <summary>Minimum gap between log records for one application.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, TerminationReport> _reports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logs the "no interface attached" condition once per episode, not once per kill.</summary>
    private readonly RepeatedDiagnostic _noInterface = new();

    private AppRuleIndex _armed = AppRuleIndex.Empty;

    private enum Verdict
    {
        Terminated,
        Refused,
        Denied,
    }

    public void Arm(AppRuleIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        lock (_sync)
        {
            _armed = index;
        }
    }

    public void Disarm()
    {
        List<TerminationReport> pending;

        lock (_sync)
        {
            _armed = AppRuleIndex.Empty;

            // Flush suppressed counts now; only a later kill would otherwise emit them.
            pending = [.. _reports.Values.Where(report => report.Suppressed > 0)];
            _reports.Clear();
        }

        foreach (var report in pending)
        {
            Announce(report.Verdict, report.FileName, report.Suppressed);
        }
    }

    /// <summary>Terminates matching applications that were already running; returns how many.</summary>
    public int SweepExisting()
    {
        AppRuleIndex armed;
        lock (_sync)
        {
            armed = _armed;
        }

        if (armed.IsEmpty)
        {
            return 0;
        }

        var killed = 0;
        foreach (var process in processes.List())
        {
            // List() already returns resolved full paths, so test both halves directly.
            var matches = armed.MatchesFileName(process.ImagePath) || armed.MatchesFullPath(process.ImagePath);
            if (matches && Terminate(process.ProcessId, process.ImagePath))
            {
                killed++;
            }
        }

        return killed;
    }

    public void OnProcessStarted(ProcessStarted started)
    {
        AppRuleIndex armed;
        lock (_sync)
        {
            armed = _armed;
        }

        if (armed.IsEmpty)
        {
            return;
        }

        // Start events carry only a bare file name. Resolve the full path only when that misses
        // and a full-path rule is armed.
        if (armed.MatchesFileName(started.ImagePath))
        {
            Terminate(started.ProcessId, started.ImagePath);
            return;
        }

        if (!armed.HasFullPathRules)
        {
            return;
        }

        var fullPath = processes.ImagePathOf(started.ProcessId);
        if (fullPath is null)
        {
            // QueryFullProcessImageName returns null both when the process exited and when access
            // is denied. Log it so a full-path rule never fails silently.
            logger.LogDebug(
                "Could not resolve the image path for process {ProcessId} to test it against the full-path rules; it may have already exited, or this account may not query it. Left alone.",
                started.ProcessId);
            return;
        }

        if (armed.MatchesFullPath(fullPath))
        {
            // Pass the resolved path on so the protection check does not resolve it again.
            Terminate(started.ProcessId, fullPath);
        }
    }

    private bool Terminate(int processId, string imagePath)
    {
        var fileName = ImagePath.FileNameOf(imagePath);
        var path = PathForProtection(processId, imagePath);

        // Re-checked here because the config file can be edited around the UI's check at add time.
        var verdict = protection.Evaluate(new AppRule(AppMatchKind.FullPath, path));
        if (verdict.IsProtected)
        {
            Report(Verdict.Refused, fileName, path, verdict.Reason);
            return false;
        }

        switch (processes.Kill(processId))
        {
            case KillOutcome.AlreadyGone:
                // Ended between the event and the kill. Expected; Debug only.
                logger.LogDebug("Process {ProcessId} for {Path} was already gone.", processId, path);
                return false;

            case KillOutcome.Denied:
                // Must be reported: the user would otherwise believe the app is blocked.
                Report(Verdict.Denied, fileName, path, reason: null);
                return false;

            case KillOutcome.Terminated:
            default:
                break;
        }

        Report(Verdict.Terminated, fileName, path, reason: null);
        AnnounceTheBlock(fileName);

        return true;
    }

    /// <summary>Publishes every termination; the interface rate-limits the block screen itself.</summary>
    private void AnnounceTheBlock(string fileName)
    {
        if (events.SubscriberCount > 0)
        {
            events.Publish(IpcEvent.AppBlocked(status(), fileName));

            // Re-arms the "no interface" record for when the interface goes away.
            _noInterface.IsNews(null);

            return;
        }

        if (_noInterface.IsNews("absent"))
        {
            // The application is deliberately not named in this record.
            logger.LogInformation(
                "A blocked application was terminated with no interface attached; no block screen was shown.");
        }
    }

    /// <summary>
    /// The WMI source reports a bare name, which cannot match the protected directories, so resolve
    /// it first. If resolution fails the name is used anyway.
    /// </summary>
    private string PathForProtection(int processId, string imagePath)
    {
        if (imagePath.IndexOfAny(['\\', '/']) >= 0)
        {
            return imagePath;
        }

        return processes.ImagePathOf(processId) ?? imagePath;
    }

    private void Report(Verdict verdict, string fileName, string imagePath, string? reason)
    {
        // The verdict is in the key so a refusal is not throttled by a denial.
        var key = $"{verdict}:{fileName}";

        var announce = false;
        int? sinceLastRecord = null;

        lock (_sync)
        {
            var now = clock.UtcNow;

            if (!_reports.TryGetValue(key, out var report))
            {
                _reports[key] = new TerminationReport(verdict, fileName, now, 0);
                announce = true;
            }
            else if (now - report.ReportedAt >= ReportInterval)
            {
                sinceLastRecord = report.Suppressed + 1;
                _reports[key] = report with { ReportedAt = now, Suppressed = 0 };
            }
            else
            {
                _reports[key] = report with { Suppressed = report.Suppressed + 1 };
            }
        }

        if (!announce && sinceLastRecord is null)
        {
            return;
        }

        // Log outside the lock: sinks can be slow.
        Announce(verdict, fileName, sinceLastRecord);

        if (reason is not null)
        {
            // Paths are allowed only at Debug. Throttled with the record above.
            logger.LogDebug("Refusing {Path}: {Reason}", imagePath, reason);
        }
    }

    /// <summary>Writes the first record of its kind, or the one carrying the suppressed count.</summary>
    private void Announce(Verdict verdict, string fileName, int? sinceLastRecord)
    {
        // The file name is enough to act on; the path stays out of every record above Debug.
        if (sinceLastRecord is not { } count)
        {
            switch (verdict)
            {
                case Verdict.Refused:
                    logger.LogWarning("Refused to terminate {Application}: it is protected.", fileName);
                    break;

                case Verdict.Denied:
                    logger.LogWarning("Could not terminate {Application}; it is still running.", fileName);
                    break;

                case Verdict.Terminated:
                default:
                    logger.LogInformation("Terminated {Application}.", fileName);
                    break;
            }

            return;
        }

        switch (verdict)
        {
            case Verdict.Refused:
                logger.LogWarning(
                    "Refused to terminate {Application} {Count} further times since the previous record; it is protected.",
                    fileName, count);
                break;

            case Verdict.Denied:
                logger.LogWarning(
                    "Could not terminate {Application} {Count} further times since the previous record; it is still running.",
                    fileName, count);
                break;

            case Verdict.Terminated:
            default:
                logger.LogInformation(
                    "Terminated {Application}, and {Count} further times since the previous record.",
                    fileName, count);
                break;
        }
    }

    private readonly record struct TerminationReport(
        Verdict Verdict,
        string FileName,
        DateTimeOffset ReportedAt,
        int Suppressed);
}
