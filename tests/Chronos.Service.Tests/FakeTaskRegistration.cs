using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>The task scheduler, remembered instead of called: a registered task would run at every boot of the machine.</summary>
internal sealed class FakeTaskRegistration : ITaskRegistration
{
    private readonly List<string> _steps;

    public FakeTaskRegistration()
        : this([])
    {
    }

    public FakeTaskRegistration(List<string> steps) => _steps = steps;

    public bool Registered { get; set; }

    /// <summary>The document the task was registered from, for the test that reads it back.</summary>
    public string? Xml { get; private set; }

    /// <summary>A scheduler that cannot be asked, which is not the same as one with no such task: the real one throws, and uninstall must treat it as a failed step.</summary>
    public Exception? ExistsThrows { get; set; }

    public Exception? RegisterThrows { get; set; }

    public Exception? RemoveThrows { get; set; }

    public bool Exists() => ExistsThrows is { } failure ? throw failure : Registered;

    public void Register(string xml)
    {
        _steps.Add("register-task");

        if (RegisterThrows is { } failure)
        {
            throw failure;
        }

        Xml = xml;
        Registered = true;
    }

    public void Remove()
    {
        _steps.Add("remove-task");

        if (RemoveThrows is { } failure)
        {
            throw failure;
        }

        Registered = false;
    }
}
