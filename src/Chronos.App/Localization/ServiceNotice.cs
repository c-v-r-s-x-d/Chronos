using System.Globalization;
using Chronos.App.Resources;
using Chronos.Ipc;

namespace Chronos.App.Localization;

/// <summary>
/// The service's refusals and warnings in the language on the screen. The service sends codes
/// because it does not know what language the person reads; every word is here.
/// </summary>
public static class ServiceNotice
{
    /// <summary>One code in words with its values put in, or the code itself when unknown or given too few values.</summary>
    public static string Text(string? code, IReadOnlyList<string>? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        if (Template(code) is not { } template)
        {
            return code;
        }

        try
        {
            return string.Format(CultureInfo.CurrentUICulture, template, [.. arguments ?? []]);
        }
        catch (FormatException)
        {
            return code;
        }
    }

    private static string? Template(string code) => code switch
    {
        IpcCodes.RequestInvalidJson => Strings.NoticeRequestInvalidJson,
        IpcCodes.RequestEmpty => Strings.NoticeRequestEmpty,
        IpcCodes.CommandUnknown => Strings.NoticeCommandUnknown,
        IpcCodes.SessionStartWrongState => Strings.NoticeSessionStartWrongState,
        IpcCodes.SessionEmptyBlockList => Strings.NoticeSessionEmptyBlockList,
        IpcCodes.SessionExtendWrongState => Strings.NoticeSessionExtendWrongState,
        IpcCodes.SessionCannotShorten => Strings.NoticeSessionCannotShorten,
        IpcCodes.SessionExtendNeedsMinutes => Strings.NoticeSessionExtendNeedsMinutes,
        IpcCodes.SessionNoneToExtend => Strings.NoticeSessionNoneToExtend,
        IpcCodes.SessionDurationClamped => Strings.NoticeSessionDurationClamped,
        IpcCodes.SessionCoolDownClamped => Strings.NoticeSessionCoolDownClamped,
        IpcCodes.SessionEndClamped => Strings.NoticeSessionEndClamped,
        IpcCodes.SessionProtectedAppSkipped => Strings.NoticeSessionProtectedAppSkipped,
        IpcCodes.UnlockRequestWrongState => Strings.NoticeUnlockRequestWrongState,
        IpcCodes.UnlockCancelWrongState => Strings.NoticeUnlockCancelWrongState,
        IpcCodes.RulesAddWrongState => Strings.NoticeRulesAddWrongState,
        IpcCodes.RulesFrozen => Strings.NoticeRulesFrozen,
        IpcCodes.RulesSiteMissing => Strings.NoticeRulesSiteMissing,
        IpcCodes.RulesSiteDomainBlank => Strings.NoticeRulesSiteDomainBlank,
        IpcCodes.RulesSiteNotFound => Strings.NoticeRulesSiteNotFound,
        IpcCodes.RulesAppMissing => Strings.NoticeRulesAppMissing,
        IpcCodes.RulesAppKindUnknown => Strings.NoticeRulesAppKindUnknown,
        IpcCodes.RulesAppValueBlank => Strings.NoticeRulesAppValueBlank,
        IpcCodes.RulesAppNotFound => Strings.NoticeRulesAppNotFound,
        IpcCodes.RulesAppProtected => Strings.NoticeRulesAppProtected,
        IpcCodes.RulesAppProtectedSystemFile => Strings.NoticeRulesAppProtectedSystemFile,
        IpcCodes.RulesAppProtectedSystemDirectory => Strings.NoticeRulesAppProtectedSystemDirectory,
        IpcCodes.RulesRemovalWaitsForNextSession => Strings.NoticeRulesRemovalWaitsForNextSession,
        IpcCodes.ConfigSettingsMissing => Strings.NoticeConfigSettingsMissing,
        IpcCodes.ConfigLanguageBlank => Strings.NoticeConfigLanguageBlank,
        IpcCodes.ConfigCoolDownClamped => Strings.NoticeConfigCoolDownClamped,
        IpcCodes.ConfigSessionDurationClamped => Strings.NoticeConfigSessionDurationClamped,
        IpcCodes.ConfigCoolDownAppliesNextSession => Strings.NoticeConfigCoolDownAppliesNextSession,
        _ => null,
    };
}
