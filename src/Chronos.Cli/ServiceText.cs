using System.Globalization;
using Chronos.Ipc;

namespace Chronos.Cli;

/// <summary>The service's codes in the CLI's English.</summary>
public static class ServiceText
{
    private const string NoCode = "(no code)";

    private static readonly HashSet<string> InMinutes = new(StringComparer.Ordinal)
    {
        IpcCodes.SessionDurationClamped,
        IpcCodes.SessionCoolDownClamped,
        IpcCodes.SessionEndClamped,
        IpcCodes.ConfigCoolDownClamped,
        IpcCodes.ConfigSessionDurationClamped,
    };

    /// <summary>The sentence for a code, or the code itself when it is unknown or lacks values.</summary>
    public static string English(string? code, IReadOnlyList<string>? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return NoCode;
        }

        if (Template(code) is not { } template)
        {
            return code;
        }

        var values = arguments ?? [];
        if (InMinutes.Contains(code) && values.Count > 0)
        {
            values = [Minutes(values[0]), .. values.Skip(1)];
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, [.. values]);
        }
        catch (FormatException)
        {
            return code;
        }
    }

    public static string English(IpcNotice? notice) => English(notice?.Code, notice?.Arguments);

    private static string Minutes(string value) => value == "1" ? "1 minute" : $"{value} minutes";

    private static string? Template(string code) => code switch
    {
        IpcCodes.RequestInvalidJson => "Request is not valid JSON.",
        IpcCodes.RequestEmpty => "Request is empty.",
        IpcCodes.CommandUnknown => "The service does not know that command.",
        IpcCodes.SessionStartWrongState => "Cannot start a session while one is running or its block is still being lifted.",
        IpcCodes.SessionEmptyBlockList => "Cannot start a session with an empty block list.",
        IpcCodes.SessionExtendWrongState => "Cannot change the end time: no session is running.",
        IpcCodes.SessionCannotShorten => "A session can only be extended, never shortened.",
        IpcCodes.SessionExtendNeedsMinutes => "ExtendSession requires a positive number of minutes.",
        IpcCodes.SessionNoneToExtend => "There is no session to extend.",
        IpcCodes.SessionDurationClamped => "Session duration was clamped to {0}.",
        IpcCodes.SessionCoolDownClamped => "Cool-down was clamped to {0}.",
        IpcCodes.SessionEndClamped => "End time was clamped to the maximum session duration of {0}.",
        IpcCodes.SessionProtectedAppSkipped => "'{0}' is protected and was skipped.",
        IpcCodes.UnlockRequestWrongState => "Cannot request unlock: no session is running, or an unlock is already pending.",
        IpcCodes.UnlockCancelWrongState => "Cannot cancel unlock: no unlock is pending.",
        IpcCodes.RulesAddWrongState => "Cannot add a rule: no session is running.",
        IpcCodes.RulesFrozen => "Cannot change the rules while a finished session is still being cleared.",
        IpcCodes.RulesSiteMissing => "This command requires a site rule.",
        IpcCodes.RulesSiteDomainBlank => "A site rule needs a domain.",
        IpcCodes.RulesSiteNotFound => "The configuration has no site rule for that domain.",
        IpcCodes.RulesAppMissing => "This command requires an app rule.",
        IpcCodes.RulesAppKindUnknown => "That is not a match kind this service knows.",
        IpcCodes.RulesAppValueBlank => "An app rule needs a value.",
        IpcCodes.RulesAppNotFound => "The configuration has no app rule for that application.",
        IpcCodes.RulesAppProtected => "That application is protected and cannot be blocked.",
        IpcCodes.RulesAppProtectedSystemFile => "That application is required by Windows or by Chronos itself.",
        IpcCodes.RulesAppProtectedSystemDirectory => "That application lives in a Windows system directory.",
        IpcCodes.RulesRemovalWaitsForNextSession =>
            "The rule was removed from the configuration. The session already running keeps it until it ends.",
        IpcCodes.ConfigSettingsMissing => "UpdateSettings requires a settings block.",
        IpcCodes.ConfigLanguageBlank => "Language cannot be blank.",
        IpcCodes.ConfigCoolDownClamped => "Cool-down was clamped to {0}.",
        IpcCodes.ConfigSessionDurationClamped => "Default session duration was clamped to {0}.",
        IpcCodes.ConfigCoolDownAppliesNextSession => "The session already running keeps the cool-down it began with.",
        _ => null,
    };
}
