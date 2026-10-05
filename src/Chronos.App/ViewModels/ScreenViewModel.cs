using Chronos.App.Localization;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chronos.App.ViewModels;

/// <summary>
/// One of the three screens. A status is news from the service; a tick is only time passing; a
/// language change is only different words. They are kept apart so a tick or a language change
/// does not redraw everything.
/// </summary>
public abstract class ScreenViewModel : ObservableObject, IDisposable
{
    protected ScreenViewModel(Text text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
        Text.Changed += OnLanguageChanged;
    }

    /// <summary>The words the markup binds to directly.</summary>
    public Text Text { get; }

    /// <summary>A new status from the service.</summary>
    public abstract void Show(StatusPayload status);

    /// <summary>
    /// The clock moved and nothing else did. Say again what is read off a clock, and only that.
    /// </summary>
    public abstract void Tick();

    /// <summary>The language changed. Say again everything this screen builds itself rather than names in the markup, and rebuild whatever is a list of words.</summary>
    public abstract void Reword();

    public void Dispose() => Text.Changed -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e) => Reword();
}
