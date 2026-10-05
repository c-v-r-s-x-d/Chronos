using System.ComponentModel;
using System.Net;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Service.Apps;
using Chronos.Service.Ipc;
using Chronos.Service.Rules;
using Chronos.Service.Sites;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

/// <summary>Whatever a pass leaves in <see cref="ReconcileResult.Detail"/> reaches the interface as a reason code, never as platform text.</summary>
public sealed class LayerReasonCodeTests : IDisposable
{
    private const string Domain = "example.com";
    private const string Foreign = "127.0.0.1 localhost\r\n";

    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDnsCache _dns = new();
    private readonly FakeWfpEngine _engine = new();
    private readonly FakeAddressResolver _resolver = new();
    private readonly ProtectedAddresses _protectedAddresses = new();
    private readonly ServiceTestClock _clock = new(Now);
    private readonly FakeProcessControl _processes = new();

    public LayerReasonCodeTests() => Directory.CreateDirectory(_root);

    private static EnforcementPlan PlanWithApp { get; } = new(
        Guid.NewGuid(),
        Now.AddHours(1),
        [],
        [new AppRule(AppMatchKind.FileName, "game.exe")]);

    private string HostsPath => Path.Combine(_root, "hosts");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Hosts_ReportsACodeWhenTheDirectoryIsGone()
    {
        var missing = new HostsEnforcer(
            new HostsFile(Path.Combine(_root, "gone", "hosts")), _dns, NullLogger<HostsEnforcer>.Instance);

        AssertIsAReasonCode(await FailureOf(missing));
    }

    [Fact]
    public async Task Hosts_ReportsACodeWhenSomethingElseHoldsTheFile()
    {
        File.WriteAllText(HostsPath, Foreign);
        using var exclusive = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        AssertIsAReasonCode(await FailureOf(Hosts()));
    }

    [Fact]
    public async Task Hosts_ReportsACodeWhenTheWriteFailsAfterTheProbeSucceeded()
    {
        File.WriteAllText(HostsPath, Foreign);
        File.SetAttributes(HostsPath, FileAttributes.ReadOnly);

        try
        {
            AssertIsAReasonCode(await FailureOf(Hosts()));
        }
        finally
        {
            File.SetAttributes(HostsPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Wfp_ReportsACodeWhenTheUserSwitchedTheLayerOff()
    {
        AssertIsAReasonCode(await FailureOf(Wfp(enabled: false)));
    }

    [Fact]
    public async Task Wfp_ReportsACodeWhenTheEngineWillNotOpen()
    {
        var refused = Wfp(engine: () => throw new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320005."));

        AssertIsAReasonCode(await FailureOf(refused));
    }

    [Fact]
    public async Task Apps_ReportsACodeWhenNoEventSourceStarts()
    {
        var apps = new AppEnforcer(
            new AppWatchdog(_processes, new WindowsProtectedAppPolicy(), _clock, new EventBus(), TestStatus.Blank, NullLogger<AppWatchdog>.Instance),
            _ => throw new InvalidOperationException("The trace session could not be started."),
            _clock,
            NullLogger<AppEnforcer>.Instance);

        AssertIsAReasonCode(await FailureOf(apps, PlanWithApp));
    }

    // The Func given to this layer calls ConfigStore.Load, which lets some exceptions escape (e.g. SecurityException).
    // It runs outside every try in the layer, so the coordinator must catch it.
    // Win32Exception(5) because its message is localized by the OS.
    [Fact]
    public async Task Wfp_ReportsACodeWhenTheEnabledFlagCannotBeRead()
    {
        var failure = new Win32Exception(5);

        var results = await Coordinator(Wfp(isEnabled: () => throw failure))
            .RunAsync(Plan(Domain), CancellationToken.None);
        var result = Assert.Single(results);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);

        // ReconcileRunner.Report still logs the exception itself.
        Assert.Same(failure, result.Error);
        AssertIsAReasonCode(result.Detail);
    }

    // A layer that cannot take its filters back throws; ClearAllAsync is what turns that into a result.
    [Fact]
    public async Task Clear_ReportsACodeWhenTheAddressLayerCannotTakeItsFiltersBack()
    {
        var wfp = Wfp();
        wfp.Dispose();

        var results = await Coordinator(wfp).ClearAllAsync(CancellationToken.None);
        var result = Assert.Single(results);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        AssertIsAReasonCode(result.Detail);
    }

    [Fact]
    public async Task Clear_ReportsACodeWhenSomethingElseHoldsTheHostsFile()
    {
        File.WriteAllText(HostsPath, Foreign);
        using var exclusive = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var results = await Coordinator(Hosts()).ClearAllAsync(CancellationToken.None);
        var result = Assert.Single(results);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        AssertIsAReasonCode(result.Detail);
    }

    private static void AssertIsAReasonCode(string? detail)
    {
        Assert.NotNull(detail);

        // The interface looks this up as a key: no capitals, no punctuation but the separators, no spaces.
        Assert.Matches("^[a-z0-9]+(\\.[a-z0-9]+(-[a-z0-9]+)*)+$", detail);
        Assert.DoesNotContain(" ", detail, StringComparison.Ordinal);
        Assert.All(detail, character => Assert.True(
            char.IsAscii(character),
            $"'{detail}' is not an identifier but text in some language: '{character}' is outside ASCII."));
    }

    private static ReconcileCoordinator Coordinator(IEnforcer enforcer) => new([enforcer]);

    private static EnforcementPlan Plan(params string[] domains) => new(
        Guid.NewGuid(),
        Now.AddHours(1),
        [.. domains.Select(domain => new SiteRule(domain, includeSubdomains: true))],
        []);

    private static async Task<string?> FailureOf(IEnforcer enforcer, EnforcementPlan? plan = null)
    {
        var results = await Coordinator(enforcer).RunAsync(plan ?? Plan(Domain), CancellationToken.None);
        var result = Assert.Single(results);

        Assert.True(
            result.Outcome is ReconcileOutcome.Skipped or ReconcileOutcome.Failed,
            $"The layer was expected to report itself unusable, not {result.Outcome}.");

        return result.Detail;
    }

    private HostsEnforcer Hosts() => new(new HostsFile(HostsPath), _dns, NullLogger<HostsEnforcer>.Instance);

    private WfpEnforcer Wfp(bool enabled = true, Func<bool>? isEnabled = null, Func<IWfpEngine>? engine = null) => new(
        isEnabled ?? (() => enabled),
        engine ?? (() => _engine),
        _resolver,
        _protectedAddresses,
        static () => (IReadOnlyList<IPAddress>)[],
        _clock,
        NullLogger<WfpEnforcer>.Instance);
}
