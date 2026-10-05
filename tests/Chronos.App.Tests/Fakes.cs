using System.Reflection;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The machine's clock, moved by hand. Countdown tests measure against it, never wall time.</summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeTicker : ITicker
{
    public event EventHandler? Ticked;

    public void Tick() => Ticked?.Invoke(this, EventArgs.Empty);
}

/// <summary>A link the test drives: it publishes the snapshots the test names and records every command sent.</summary>
internal sealed class RecordingLink : IServiceLink
{
    private readonly List<IpcRequest> _sent = [];

    public ServiceSnapshot Snapshot { get; private set; } = ServiceSnapshot.Connecting;

    public event EventHandler<ServiceSnapshot>? Changed;

    public event EventHandler<BlockEvent>? Blocked;

    public event EventHandler<TimeSpan>? Retrying;

    public void PublishRetry(TimeSpan wait) => Retrying?.Invoke(this, wait);

    public IReadOnlyList<IpcRequest> Sent => _sent.ToArray();

    /// <summary>What the service does about a command. The real one answers, then publishes the new status; screens change on the second half.</summary>
    public Action<IpcRequest>? Service { get; set; }

    /// <summary>What the service answers. The default is an accepted command with nothing in it.</summary>
    public Func<IpcRequest, IpcResponse?>? Answer { get; set; }

    public void Publish(ServiceSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    /// <summary>A refusal. Does not touch <see cref="Snapshot"/>: the block screen is built from the event's status, and the two must not agree by construction.</summary>
    public void PublishBlock(BlockEvent block) => Blocked?.Invoke(this, block);

    public Task<IpcResponse?> SendAsync(IpcRequest request, CancellationToken ct)
    {
        _sent.Add(request);
        Service?.Invoke(request);

        return Task.FromResult(Answer is null ? IpcResponse.Ok() : Answer(request));
    }
}

/// <summary>Statuses shaped the way the service shapes them, with only the fields a screen reads.</summary>
internal static class Say
{
    /// <param name="sites">The rules of the running session; empty with no session however full the user's list is.</param>
    public static StatusPayload Status(
        string state,
        DateTimeOffset now,
        DateTimeOffset? endsAt = null,
        DateTimeOffset? unlockAt = null,
        int coolDownMinutes = 30,
        IReadOnlyList<string>? sites = null,
        IReadOnlyList<string>? apps = null,
        IReadOnlyList<LayerStatus>? layers = null,
        DateTimeOffset? startedAt = null) =>
        new(
            state,
            now,
            startedAt,
            endsAt,
            unlockAt,
            coolDownMinutes,
            [.. (sites ?? []).Select(domain => new SiteRuleMessage(domain, true))],
            [.. (apps ?? []).Select(value => new AppRuleMessage("FileName", value))],
            layers ?? []);

    public static LayerStatus Layer(string name, bool available = true, string? reason = null) =>
        new(name, available, reason, available ? "applied" : "failed", DateTimeOffset.UnixEpoch);

    public static ConfigPayload Config(
        IReadOnlyList<string>? sites = null,
        IReadOnlyList<string>? apps = null,
        int defaultMinutes = 60,
        int coolDownMinutes = 30) =>
        new(
            [.. (sites ?? []).Select(domain => new SiteRuleMessage(domain, true))],
            [.. (apps ?? []).Select(value => new AppRuleMessage("FileName", value))],
            coolDownMinutes,
            defaultMinutes,
            "en",
            false,
            true);
}

/// <summary>The shell and the three screens, wired to a clock and a beat the test owns.</summary>
internal sealed class AppUnderTest : IDisposable
{
    /// <param name="quit">What ending the process would do. Counted, so a test can see it is not reached before confirmation.</param>
    public AppUnderTest(DateTimeOffset machineNow, Action? quit = null)
    {
        Clock = new FakeClock(machineNow);
        Text = new Text(Language);
        Shell = new ShellViewModel(Link, Text, Clock, Ticker, quit);
    }

    public RecordingLink Link { get; } = new();

    public FakeClock Clock { get; }

    public FakeTicker Ticker { get; } = new();

    public LanguageSwitch Language { get; } = new();

    public Text Text { get; }

    public ShellViewModel Shell { get; }

    public void Show(StatusPayload status) => Link.Publish(ServiceSnapshot.Available(status));

    public void Dispose() => Shell.Dispose();
}

internal static class ShellExtensions
{
    public static SetupViewModel SetupScreen(this ShellViewModel shell) =>
        Assert.IsType<SetupViewModel>(shell.CurrentScreen);
}

/// <summary>Files for tests that pick a program or a shortcut. One folder per run, removed when the process ends.</summary>
internal static class Fixtures
{
    private static readonly Lazy<string> Folder = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "chronos-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // The machine's business, not this test's.
            }
        };

        return path;
    });

    private static readonly Lazy<string> Program = new(() =>
    {
        var path = Path.Combine(Folder.Value, "notepad.exe");
        File.WriteAllText(path, "a file, for the purposes of this test");

        return path;
    });

    public static string Notepad => Program.Value;

    /// <summary>A real shortcut written through the Windows Script Host, not through this product's code.</summary>
    /// <param name="path">Where to write it; a fresh name in the fixture folder when not given.</param>
    public static string ShortcutTo(string target, string? path = null)
    {
        path ??= Path.Combine(Folder.Value, Guid.NewGuid().ToString("N") + ".lnk");

        var type = Type.GetTypeFromProgID("WScript.Shell");
        Assert.True(type is not null, "WScript.Shell is not registered on this machine.");

        var shell = Activator.CreateInstance(type!);
        Assert.NotNull(shell);

        var link = shell!.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
        Assert.NotNull(link);

        link!.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [target]);
        link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, []);

        return path;
    }
}
