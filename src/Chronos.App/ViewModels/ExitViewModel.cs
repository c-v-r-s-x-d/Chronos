using Chronos.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chronos.App.ViewModels;

/// <summary>
/// The way out of the product, and what taking it costs. Quitting does not lift the block, but the
/// block screens and notices stop; the warning says both halves. The decision lives here so a test
/// can watch it without a windowing system.
/// </summary>
public sealed class ExitViewModel : ObservableObject
{
    private readonly Func<bool> _blockIsInForce;

    private readonly Action _quit;

    private bool _isWarningShowing;

    private bool _isQuitting;

    /// <param name="blockIsInForce">
    /// Whether leaving now would leave a block behind. Asked at the moment the exit is asked for
    /// rather than remembered, because a session can end between opening the menu and choosing.
    /// </param>
    /// <param name="quit">Ends the process. Reached only through this object, and only once.</param>
    public ExitViewModel(Text text, Func<bool> blockIsInForce, Action quit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(blockIsInForce);
        ArgumentNullException.ThrowIfNull(quit);

        Text = text;
        _blockIsInForce = blockIsInForce;
        _quit = quit;

        RequestCommand = new RelayCommand(Request);
        ConfirmCommand = new RelayCommand(Confirm);
        CancelCommand = new RelayCommand(Cancel);
    }

    public Text Text { get; }

    /// <summary>The question is on the screen and has not been answered.</summary>
    public bool IsWarningShowing
    {
        get => _isWarningShowing;
        private set => SetProperty(ref _isWarningShowing, value);
    }

    /// <summary>Somebody is leaving. The window cancels its own closing while this is false, so the process outlives it, and stops cancelling once it is true.</summary>
    public bool IsQuitting
    {
        get => _isQuitting;
        private set => SetProperty(ref _isQuitting, value);
    }

    /// <summary>Asks to leave. Whether that is a question or a departure depends on the session.</summary>
    public IRelayCommand RequestCommand { get; }

    public IRelayCommand ConfirmCommand { get; }

    public IRelayCommand CancelCommand { get; }

    private void Request()
    {
        if (IsQuitting)
        {
            return;
        }

        if (_blockIsInForce())
        {
            // And nothing else: the process is still here.
            IsWarningShowing = true;

            return;
        }

        // Nothing to warn about, so no dialog: it would teach people to click through.
        Quit();
    }

    private void Confirm()
    {
        // Only ever an answer to a question that was asked. Without one there is nothing to
        // confirm, and a stray command must not be a way past the warning.
        if (!IsWarningShowing)
        {
            return;
        }

        IsWarningShowing = false;
        Quit();
    }

    private void Cancel() => IsWarningShowing = false;

    private void Quit()
    {
        IsQuitting = true;
        _quit();
    }
}
