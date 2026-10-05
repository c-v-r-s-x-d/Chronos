using System.Globalization;
using Chronos.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chronos.App.Localization;

/// <summary>The words on the screen, one property per resource key. A property, not <c>x:Static</c>, so the markup sees a language change.</summary>
public sealed class Text : ObservableObject
{
    // Every property, found once. A hand-written list is what rots the first time a label is added.
    private static readonly string[] Everything =
        typeof(Text).GetProperties().Select(property => property.Name).ToArray();

    private readonly LanguageSwitch _language;

    public Text(LanguageSwitch language)
    {
        ArgumentNullException.ThrowIfNull(language);

        _language = language;
        language.Changed += (_, _) => Announce();
    }

    /// <summary>Which language the screen is in, as the stored choice. Reached through here so a screen cannot get words from one switch and a choice from another.</summary>
    public string Language => _language.Choice;

    /// <summary>Everything here answers in the new language from the next read, and every property is announced at once.</summary>
    public void SwitchTo(string? choice) => _language.Choose(choice);

    public string AppTitle => Strings.AppTitle;

    public string ServiceAvailable => Strings.ServiceAvailable;

    public string ServiceConnecting => Strings.ServiceConnecting;

    // What happened, what it means, what to run, and when the next try is.
    public string ServiceUnavailableHeading => Strings.ServiceUnavailableHeading;

    public string ServiceUnavailableMeaning => Strings.ServiceUnavailableMeaning;

    public string ServiceRecoverHint => Strings.ServiceRecoverHint;

    public string ServiceRetryNow => Strings.ServiceRetryNow;

    /// <summary>«Следующая попытка через …» - «через» takes the accusative.</summary>
    public string RetryIn(TimeSpan left) =>
        Fill(Strings.ServiceRetryFormat, left, GrammaticalCase.Accusative);

    // The sidebar and the settings cards.
    public string SectionSession => Strings.SectionSession;

    public string SectionSites => Strings.SectionSites;

    public string SectionApps => Strings.SectionApps;

    public string SectionSettings => Strings.SectionSettings;

    public string ProtectionHeading => Strings.ProtectionHeading;

    public string GeneralHeading => Strings.GeneralHeading;

    public string VersionLabel => Strings.VersionLabel;

    public string BlockingMethodsHeading => Strings.BlockingMethodsHeading;

    public string ProtectionOn => Strings.ProtectionOn;

    public string ProtectionPartial => Strings.ProtectionPartial;

    public string ProtectionOff => Strings.ProtectionOff;

    public string ProtectionUnknown => Strings.ProtectionUnknown;

    /// <summary>The lock's tooltip.</summary>
    public string RemovalUnavailableShort => Strings.RemovalUnavailableShort;

    public string NewSessionHeading => Strings.NewSessionHeading;

    public string NothingToBlock => Strings.NothingToBlock;

    public string CustomDuration => Strings.CustomDuration;

    public string GoToSites => Strings.GoToSites;

    public string GoToApps => Strings.GoToApps;

    public string MinutesLabel => Strings.MinutesLabel;

    /// <summary>
    /// "Blocks 12 sites and 3 apps until 18:45." A list with nothing in it is left out
    /// of the sentence rather than counted as zero.
    /// </summary>
    public string Summary(int sites, int apps, DateTimeOffset end)
    {
        var until = end.ToLocalTime().ToString("t", CultureInfo.CurrentUICulture);
        var ui = CultureInfo.CurrentUICulture;
        var siteWords = string.Create(ui, $"{sites} {Plural.Sites(sites)}");
        var appWords = string.Create(ui, $"{apps} {Plural.Apps(apps)}");

        return (sites, apps) switch
        {
            (_, 0) => string.Format(ui, Strings.SessionSummaryOneFormat, siteWords, until),
            (0, _) => string.Format(ui, Strings.SessionSummaryOneFormat, appWords, until),
            _ => string.Format(ui, Strings.SessionSummaryFormat, siteWords, appWords, until),
        };
    }

    public string StartSession => Strings.StartSession;

    /// <summary>What the layer list says when empty. Empty means nobody has looked yet, which differs from "nothing works" and "everything works".</summary>
    public string LayersUnknown => Strings.LayersUnknown;

    public string SitesHeading => Strings.SitesHeading;

    public string AppsHeading => Strings.AppsHeading;

    public string SearchPlaceholder => Strings.SearchPlaceholder;

    /// <summary>What a preset did, as two numbers to put into it: how many were added and how many were there.</summary>
    public string PresetOutcomeFormat => Strings.PresetOutcomeFormat;

    /// <summary>The language picker's heading. The names beside it are the languages' own and the same in either.</summary>
    public string LanguageHeading => Strings.LanguageHeading;

    public string RemoveRule => Strings.RemoveRule;

    /// <summary>A remove button as a screen reader says it, with the rule left to put in.</summary>
    public string RemoveRuleFormat => Strings.RemoveRuleFormat;

    public string AddRule => Strings.AddRule;

    public string NewSitePlaceholder => Strings.NewSitePlaceholder;

    public string NewAppPlaceholder => Strings.NewAppPlaceholder;

    /// <summary>Match kind is offered with a sentence: by name and by path look identical when chosen and behave differently afterwards.</summary>
    public string MatchKindHeading => Strings.MatchKindHeading;

    /// <summary>The "Add application" dialog: its button, title, tabs and the two buttons inside.</summary>
    public string AddApp => Strings.AddApp;

    public string AddAppTitle => Strings.AddAppTitle;

    public string TabRunning => Strings.TabRunning;

    public string TabExecutable => Strings.TabExecutable;

    public string TabShortcut => Strings.TabShortcut;

    public string TabName => Strings.TabName;

    public string ChooseFile => Strings.ChooseFile;

    /// <summary>Where a chosen shortcut leads, with the program's path left to put in.</summary>
    public string ShortcutPointsTo => Strings.ShortcutPointsTo;

    public string Cancel => Strings.Cancel;

    /// <summary>Said beside the disabled button: a hidden control looks broken.</summary>
    public string RemovalUnavailable => Strings.RemovalUnavailable;

    /// <summary>The same for the end time, said where the extending is.</summary>
    public string ShorteningUnavailable => Strings.ShorteningUnavailable;

    public string ExtendSession => Strings.ExtendSession;

    public string ListEmpty => Strings.ListEmpty;

    /// <summary>Shown while the DNS layer is unavailable or unreported: no site notice can come, and silence would look like a broken block.</summary>
    public string SiteNoticesUnavailable => Strings.SiteNoticesUnavailable;

    public string ActiveHeading => Strings.ActiveHeading;

    public string SessionFinishing => Strings.SessionFinishing;

    public string RequestUnlock => Strings.RequestUnlock;

    public string CancelUnlock => Strings.CancelUnlock;

    /// <summary>The end of a session as a sentence, with the time of day left to put in.</summary>
    public string EndsAtFormat => Strings.EndsAtFormat;

    public string ExtendHeading => Strings.ExtendHeading;

    public string CustomExtend => Strings.CustomExtend;

    public string UnlockHeading => Strings.UnlockHeading;

    /// <summary>The caption under the session's digits, alone and with the end time.</summary>
    public string RemainingCaption => Strings.RemainingCaption;

    public string RemainingCaptionFormat => Strings.RemainingCaptionFormat;

    /// <summary>How long is left, as a sentence. A method because the number comes from a clock; a screen calls it again when the language changes. «Осталось» takes the nominative.</summary>
    public string SessionRemaining(TimeSpan left) =>
        Fill(Strings.SessionRemainingFormat, left, GrammaticalCase.Nominative);

    /// <summary>The executable name goes in; it is from the user's own list, so it may be shown where a path may not.</summary>
    public string BlockedAppFormat => Strings.BlockedAppFormat;

    /// <summary>The domain an attempt asked for; shown here, never logged above Debug.</summary>
    public string BlockedSiteFormat => Strings.BlockedSiteFormat;

    /// <summary>The way off the block screen. There is nothing to decide on it, only to read.</summary>
    public string BlockDismiss => Strings.BlockDismiss;

    /// <summary>
    /// When the block lifts. «Блокировка снимется через …» - and «через» takes the accusative, so
    /// this sentence and the next one ask for a different form of the same length of time.
    /// </summary>
    public string UnlockRemaining(TimeSpan left) =>
        Fill(Strings.UnlockRemainingFormat, left, GrammaticalCase.Accusative);

    /// <summary>What asking for the unlock will cost in waiting. «через» again.</summary>
    public string CoolDownNotice(TimeSpan wait) =>
        Fill(Strings.CoolDownNoticeFormat, wait, GrammaticalCase.Accusative);

    /// <summary>The refusal for a protected app, worded here because this end made it. The service's refusals arrive already written and are shown as they are.</summary>
    public string ProtectedAppFormat => Strings.ProtectedAppFormat;


    /// <summary>The notification area. The tooltip is built from the product name and one of these fragments; Windows truncates a tooltip past 127 characters silently.</summary>
    public string TrayActive(TimeSpan left) =>
        Fill(Strings.TrayActiveFormat, left, GrammaticalCase.Nominative);

    public string TrayIdle => Strings.TrayIdle;

    public string TrayConnecting => Strings.TrayConnecting;

    /// <summary>Neither icon would be true: the service has not said what is blocked.</summary>
    public string TrayUnknown => Strings.TrayUnknown;

    /// <summary>The tray menu. The window hides to the tray, so there has to be a way back.</summary>
    public string TrayOpen => Strings.TrayOpen;

    public string TrayExit => Strings.TrayExit;

    /// <summary>Quitting does not lift the block, but the block screens and notices stop, and this says so.</summary>
    public string ExitWarningHeading => Strings.ExitWarningHeading;

    public string ExitWarning => Strings.ExitWarning;

    public string ExitConfirm => Strings.ExitConfirm;

    public string ExitCancel => Strings.ExitCancel;

    /// <summary>One sentence with a length of time in it. The case is named here, beside the sentence that needs it, so callers need no grammar. The number follows the screen's language, not the machine's culture.</summary>
    private static string Fill(string sentence, TimeSpan length, GrammaticalCase form) =>
        string.Format(CultureInfo.CurrentUICulture, sentence, Duration.Say(length, form));

    /// <summary>The language changed, once. Rebuild from this, not from <see cref="ObservableObject.PropertyChanged"/>, which fires once per label.</summary>
    public event EventHandler? Changed;

    private void Announce()
    {
        foreach (var property in Everything)
        {
            OnPropertyChanged(property);
        }

        // After the properties, so anything rebuilding itself here reads the new words rather than
        // racing the announcement that says they are there.
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
