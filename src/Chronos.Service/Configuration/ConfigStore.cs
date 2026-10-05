using System.Globalization;
using System.Text.Json;
using Chronos.Core.Sessions;
using Chronos.Service.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Configuration;

public sealed class ConfigStore(ChronosPaths paths, ILogger<ConfigStore> logger)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Load runs every reconcile pass, and its problems are often permanent. Each of these logs a
    // cause at full level once; identical repeats drop to Debug. The store is shared by several
    // callers, so the state lives here.
    private readonly RepeatedDiagnostic _readFailure = new();
    private readonly RepeatedDiagnostic _coolDownClamp = new();
    private readonly RepeatedDiagnostic _durationClamp = new();

    public ChronosConfig Load()
    {
        ChronosConfig? parsed = null;
        Exception? readFailure = null;

        if (File.Exists(paths.ConfigFile))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<ChronosConfig>(File.ReadAllText(paths.ConfigFile), Options);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                readFailure = exception;
            }
        }

        // Called on every path so a cleared failure is reported in full if it returns.
        ReportReadFailure(readFailure);

        // Defaults are in range, so a missing or unreadable file clamps nothing.
        return Clamp(parsed ?? new ChronosConfig());
    }

    public void Save(ChronosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        AtomicFile.Write(paths.ConfigFile, JsonSerializer.Serialize(config, Options));
    }

    private void ReportReadFailure(Exception? failure)
    {
        var news = _readFailure.IsNews(failure is null ? null : $"{failure.GetType().FullName}: {failure.Message}");

        if (failure is null)
        {
            return;
        }

        if (news)
        {
            logger.LogError(failure, "Configuration file is unreadable; falling back to defaults.");
        }
        else
        {
            logger.LogDebug(failure, "Configuration file is still unreadable; falling back to defaults.");
        }
    }

    private ChronosConfig Clamp(ChronosConfig config)
    {
        var coolDown = SessionLimits.ClampCoolDown(TimeSpan.FromMinutes(config.CoolDownMinutes));
        var applied = (int)coolDown.Value.TotalMinutes;

        // One message template per setting, so the log can be filtered by setting.
        switch (WhetherToReport(_coolDownClamp, coolDown.WasClamped ? config.CoolDownMinutes : null))
        {
            case Report.InFull:
                logger.LogInformation(
                    "Configured cool-down of {Configured} minutes is outside the allowed range and was corrected to {Applied} minutes.",
                    config.CoolDownMinutes, applied);
                break;

            case Report.AsARepeat:
                logger.LogDebug(
                    "Configured cool-down of {Configured} minutes is still outside the allowed range and was corrected to {Applied} minutes.",
                    config.CoolDownMinutes, applied);
                break;

            case Report.NotAtAll:
            default:
                break;
        }

        var duration = SessionLimits.ClampSessionDuration(TimeSpan.FromMinutes(config.DefaultSessionMinutes));
        var appliedDuration = (int)duration.Value.TotalMinutes;

        switch (WhetherToReport(_durationClamp, duration.WasClamped ? config.DefaultSessionMinutes : null))
        {
            case Report.InFull:
                logger.LogInformation(
                    "Configured session duration of {Configured} minutes is outside the allowed range and was corrected to {Applied} minutes.",
                    config.DefaultSessionMinutes, appliedDuration);
                break;

            case Report.AsARepeat:
                logger.LogDebug(
                    "Configured session duration of {Configured} minutes is still outside the allowed range and was corrected to {Applied} minutes.",
                    config.DefaultSessionMinutes, appliedDuration);
                break;

            case Report.NotAtAll:
            default:
                break;
        }

        return config with
        {
            CoolDownMinutes = applied,
            DefaultSessionMinutes = appliedDuration,
        };
    }

    private static Report WhetherToReport(RepeatedDiagnostic diagnostic, int? configured)
    {
        var news = diagnostic.IsNews(configured?.ToString(CultureInfo.InvariantCulture));

        if (configured is null)
        {
            return Report.NotAtAll;
        }

        return news ? Report.InFull : Report.AsARepeat;
    }

    private enum Report
    {
        NotAtAll,

        /// <summary>First occurrence, or different from the last: logged at its own level.</summary>
        InFull,

        /// <summary>Same as the last report: logged at Debug.</summary>
        AsARepeat,
    }
}
