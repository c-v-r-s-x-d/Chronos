using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Chronos.App.ViewModels;

namespace Chronos.App;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Closing the window hides it: the block screens and notices are drawn by this process. A close during exit is let through.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is ShellViewModel shell && !shell.Exit.IsQuitting)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
