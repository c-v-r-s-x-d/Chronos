using Chronos.App.Services;

namespace Chronos.App.ViewModels;

/// <summary>The three screens; there is no fourth.</summary>
internal enum ScreenKind
{
    /// <summary>No session. Where one is set up and started.</summary>
    Setup,

    /// <summary>A session is running, or finishing.</summary>
    Active,

    /// <summary>The unlock has been asked for and has not taken effect.</summary>
    Waiting,
}

internal static class Screen
{
    /// <summary>Which screen a state belongs on, or null for a word this build does not know. Ended shares the active screen: it lasts seconds and is not a state of the product.</summary>
    public static ScreenKind? For(string state) => state switch
    {
        EngineState.Idle => ScreenKind.Setup,
        EngineState.Active or EngineState.Ended => ScreenKind.Active,
        EngineState.UnlockPending => ScreenKind.Waiting,
        _ => null,
    };
}
