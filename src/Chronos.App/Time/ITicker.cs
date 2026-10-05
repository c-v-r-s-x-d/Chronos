namespace Chronos.App.Time;

/// <summary>The beat that redraws a countdown, raised on the interface thread. Nothing arrives between service events to say the time has changed.</summary>
public interface ITicker
{
    event EventHandler? Ticked;
}
