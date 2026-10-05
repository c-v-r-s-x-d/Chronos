using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>The autostart entry, remembered instead of written: the real one lives under the Run key of whoever runs the tests.</summary>
internal sealed class FakeAutostartEntry : IAutostartEntry
{
    private readonly List<string> _steps;

    public FakeAutostartEntry()
        : this([])
    {
    }

    public FakeAutostartEntry(List<string> steps) => _steps = steps;

    public bool Enabled { get; set; }

    public Exception? DisableThrows { get; set; }

    public bool IsEnabled() => Enabled;

    public void Disable()
    {
        _steps.Add("remove-autostart");

        if (DisableThrows is { } failure)
        {
            throw failure;
        }

        Enabled = false;
    }
}
