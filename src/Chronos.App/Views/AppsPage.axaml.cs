using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Chronos.App.ViewModels;

namespace Chronos.App.Views;

/// <summary>The application list. Adding to it happens in a dialog of its own.</summary>
public partial class AppsPage : UserControl
{
    public AppsPage() => AvaloniaXamlLoader.Load(this);

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppsSection section || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        // Over the screen that is up now, and let go of with the window: it listens to the language.
        using var vm = new AddAppViewModel(section.Rules);

        await new AddAppDialog { DataContext = vm, Icon = owner.Icon }.ShowDialog(owner).ConfigureAwait(true);
    }
}
