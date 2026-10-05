using Avalonia.Threading;

namespace Chronos.App.Time;

/// <summary>The beat, once a second, on the interface thread. The only part of the countdown that knows Avalonia, so screens test without a windowing system.</summary>
public sealed class DispatcherTicker : ITicker, IDisposable
{
    private readonly DispatcherTimer _timer;

    public DispatcherTicker()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public event EventHandler? Ticked;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e) => Ticked?.Invoke(this, EventArgs.Empty);
}
