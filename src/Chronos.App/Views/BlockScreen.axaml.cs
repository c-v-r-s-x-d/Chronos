using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Chronos.App.ViewModels;

namespace Chronos.App.Views;

/// <summary>The window shown for a block. Opened by <see cref="Chronos.App.Blocking.BlockNotices"/> and closed by the user; it decides nothing, so there is no command behind the button.</summary>
public partial class BlockScreen : Window
{
    public BlockScreen() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The countdown stops being fed when the window goes. Left subscribed, one of these per block
    /// would keep answering the tick for as long as the interface ran.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (DataContext is BlockViewModel model)
        {
            model.Dispose();
        }
    }

    private void Dismiss(object? sender, RoutedEventArgs e) => Close();
}
