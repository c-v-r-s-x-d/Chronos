using System.Globalization;
using Chronos.App.Diagnostics;

namespace Chronos.App.Localization;

/// <summary>
/// Owns the interface language: it sets the culture the resources are read through and
/// then says so, which is the one thing a resource file cannot do for itself.
/// </summary>
public sealed class LanguageSwitch
{
    private readonly AppLog _log;

    /// <summary>The machine's culture before anything here touched it, read once: <see cref="CultureInfo.CurrentUICulture"/> is what this class writes to.</summary>
    private readonly CultureInfo _machine = CultureInfo.CurrentUICulture;

    private readonly Action<Action>? _post;

    /// <param name="log">Where the change is written down, in English.</param>
    /// <param name="post">
    /// Queues work on the UI thread. The culture lives in the execution context, so a switch made
    /// after an <c>await</c> is undone when that async method returns; queued with the context's flow
    /// suppressed, it is set on the thread itself and stays. Null applies the switch at once.
    /// </param>
    public LanguageSwitch(AppLog? log = null, Action<Action>? post = null)
    {
        _log = log ?? AppLog.Silent;
        _post = post;
    }

    /// <summary>The language the interface is in. At startup that is whatever the machine is in.</summary>
    public CultureInfo Current { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>The language in use, as "en" or "ru": what the picker shows.</summary>
    public string Choice { get; private set; } = LanguageChoice.ForMachine(CultureInfo.CurrentUICulture);

    public event EventHandler? Changed;

    /// <summary>
    /// A stored choice. English and Russian are taken as they are; anything else - "system", nothing,
    /// a language this interface doesn't have - becomes Russian on a Russian machine and English elsewhere.
    /// </summary>
    public void Choose(string? choice)
    {
        Choice = LanguageChoice.Known(choice) ?? LanguageChoice.ForMachine(_machine);

        Use(Choice);
    }

    public void Use(string language) => Use(CultureInfo.GetCultureInfo(language));

    public void Use(CultureInfo language)
    {
        ArgumentNullException.ThrowIfNull(language);

        var was = Current;

        // Nothing to say. The configuration is read back on every status, so announcing an unmade
        // change would redraw every screen once a second.
        if (was.Equals(language))
        {
            return;
        }

        Current = language;

        if (_post is null)
        {
            Apply(was, language);

            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            _post(() => Apply(was, language));
        }
    }

    private void Apply(CultureInfo was, CultureInfo language)
    {
        // Two settings: one for this thread, one for every thread started after it.
        CultureInfo.CurrentUICulture = language;
        CultureInfo.DefaultThreadCurrentUICulture = language;

        // By code, not name: CultureInfo.NativeName would write Russian into the English log.
        _log.LanguageChanged(was.Name, language.Name);

        // CurrentCulture is left alone: it formats numbers and dates, logs included, which stay English.
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
