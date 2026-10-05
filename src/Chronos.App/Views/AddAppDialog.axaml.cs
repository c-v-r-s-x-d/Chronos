using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Chronos.App.Resources;
using Chronos.App.ViewModels;

namespace Chronos.App.Views;

/// <summary>
/// The "Add application" dialog, and the two ways of adding a program that need a picker. The picker
/// is here because it is a control; a view model owning one could not be tested without a windowing system.
/// </summary>
public partial class AddAppDialog : Window
{
    private AddAppViewModel? _shown;

    public AddAppDialog() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_shown is not null)
        {
            _shown.Done -= OnDone;
        }

        _shown = DataContext as AddAppViewModel;

        if (_shown is not null)
        {
            _shown.Done += OnDone;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The running tab is the one in front, so its list is asked for as the window comes up.
        _shown?.LoadRunningCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_shown is not null)
        {
            _shown.Done -= OnDone;
        }

        base.OnClosed(e);
    }

    // The rule is in: nothing left to do here. A refusal never gets this far.
    private void OnDone(object? sender, EventArgs e) => Close();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnChooseExecutable(object? sender, RoutedEventArgs e)
    {
        if (await PickAsync(Strings.ChooseExecutable, Strings.ExecutableFiles, "*.exe").ConfigureAwait(true) is { } file)
        {
            _shown?.ChooseExecutable(file);
        }
    }

    private async void OnChooseShortcut(object? sender, RoutedEventArgs e)
    {
        if (await PickAsync(Strings.ChooseShortcut, Strings.ShortcutFiles, "*.lnk").ConfigureAwait(true) is { } file)
        {
            // Followed to the program it starts, in the view model. A rule on the .lnk itself would
            // be accepted, would look like a rule, and would block nothing at all.
            _shown?.ChooseShortcut(file);
        }
    }

    /// <summary>One file, or null when the picker was closed with nothing chosen.</summary>
    private async Task<string?> PickAsync(string title, string kind, string pattern)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(kind) { Patterns = [pattern] }],
        }).ConfigureAwait(true);

        // A file on this machine or nothing. The picker can hand back something with no path of its
        // own - a location in a cloud provider - and there is no rule to be made from one of those.
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath();
    }
}
