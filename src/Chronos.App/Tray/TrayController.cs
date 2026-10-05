using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using Chronos.App.ViewModels;

namespace Chronos.App.Tray;

/// <summary>
/// The icon in the notification area and its small menu. What it shows is decided by
/// <see cref="TrayViewModel"/>; this is the part that needs a windowing system. Built in code because
/// a binding would need a converter for three cases.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private static readonly Uri Blocking = new("avares://Chronos.App/Assets/tray-on-32.png");

    private static readonly Uri Idle = new("avares://Chronos.App/Assets/tray-off-32.png");

    /// <summary>The product's own mark, for when the service has not said which of the other two is true.</summary>
    private static readonly Uri Unknown = new("avares://Chronos.App/Assets/chronos.ico");

    private readonly TrayViewModel _model;

    private readonly TrayIcon _icon;

    private readonly NativeMenuItem _open;

    private readonly NativeMenuItem _exit;

    public TrayController(TrayViewModel model, ExitViewModel exit, Action open)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(open);

        _model = model;

        // No literal here either: the menu's words come from the same resources as every label.
        _open = new NativeMenuItem(model.Text.TrayOpen);
        _open.Click += (_, _) => open();

        _exit = new NativeMenuItem(model.Text.TrayExit);
        _exit.Click += (_, _) =>
        {
            // The exit logic decides whether this asks or leaves; the question has to be somewhere visible.
            exit.RequestCommand.Execute(null);

            if (exit.IsWarningShowing)
            {
                open();
            }
        };

        _icon = new TrayIcon
        {
            Menu = [_open, _exit],
            IsVisible = true,
        };

        // Getting the window back has to be the obvious gesture, not a menu to find: closing it only hides it.
        _icon.Clicked += (_, _) => open();

        model.PropertyChanged += OnModelChanged;
        Draw();
    }

    /// <summary>The control itself, for the lifetime to hold on to. Nothing else reads it.</summary>
    public TrayIcon Icon => _icon;

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;

        _icon.IsVisible = false;
        _icon.Dispose();
    }

    private static WindowIcon Picture(Uri asset) => new(AssetLoader.Open(asset));

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Draw();

    private void Draw()
    {
        _icon.ToolTipText = _model.Tooltip;
        _icon.Icon = Picture(_model.Glyph switch
        {
            TrayGlyph.Blocking => Blocking,
            TrayGlyph.Idle => Idle,
            _ => Unknown,
        });

        // The menu's words change with the language too, and a native menu is not redrawn from a
        // binding the way a control is.
        _open.Header = _model.Text.TrayOpen;
        _exit.Header = _model.Text.TrayExit;
    }
}
