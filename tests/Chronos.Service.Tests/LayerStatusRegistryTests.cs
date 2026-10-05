using Chronos.Core.Enforcement;
using Chronos.Service.Reconciliation;

namespace Chronos.Service.Tests;

public sealed class LayerStatusRegistryTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 23, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Current_IsEmptyBeforeAnyPass()
    {
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        Assert.Empty(registry.Current());
    }

    [Fact]
    public void Current_LeavesOutALayerNoPassHasReportedOn()
    {
        // Unknown and available differ: "no pass yet" is not a reason a layer does not work.
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        registry.Record([ReconcileResult.Unchanged("hosts")]);

        Assert.Equal(["hosts"], registry.Current().Select(layer => layer.Name));
    }

    [Fact]
    public void Current_ReportsTheLayerUnavailableWithTheReasonFromTheLastPass()
    {
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        registry.Record([ReconcileResult.Skipped("wfp", "wfp.engine-unavailable")]);

        var layer = Assert.Single(registry.Current());
        Assert.Equal("wfp", layer.Name);
        Assert.False(layer.IsAvailable);
        Assert.Equal("wfp.engine-unavailable", layer.ReasonCode);
        Assert.Equal(Moment, layer.ObservedAt);
    }

    [Fact]
    public void Current_ReportsALayerThatFailedAsUnavailableWithTheFailureAsTheReason()
    {
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        registry.Record([ReconcileResult.Failed("hosts", "hosts.not-writable")]);

        var layer = Assert.Single(registry.Current());
        Assert.False(layer.IsAvailable);
        Assert.Equal("hosts.not-writable", layer.ReasonCode);
        Assert.Equal(nameof(ReconcileOutcome.Failed), layer.LastOutcome);
    }

    [Fact]
    public void Current_ReportsALayerAvailableAgainAfterAPassThatSucceeded()
    {
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        registry.Record([ReconcileResult.Failed("hosts", "hosts.not-writable")]);
        registry.Record([ReconcileResult.Changed("hosts", 3, 0)]);

        var layer = Assert.Single(registry.Current());
        Assert.True(layer.IsAvailable);
        Assert.Null(layer.ReasonCode);
        Assert.Equal(nameof(ReconcileOutcome.Changed), layer.LastOutcome);
    }

    [Fact]
    public void Current_ReportsAPassThatChangedNothingAsAvailable()
    {
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        registry.Record([ReconcileResult.Unchanged("apps")]);

        var layer = Assert.Single(registry.Current());
        Assert.True(layer.IsAvailable);
        Assert.Null(layer.ReasonCode);
        Assert.Equal(nameof(ReconcileOutcome.Unchanged), layer.LastOutcome);
    }

    [Fact]
    public void Current_KeepsTheOrderTheLayersRunIn()
    {
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        registry.Record(
        [
            ReconcileResult.Unchanged("hosts"),
            ReconcileResult.Skipped("apps", "No process events."),
            ReconcileResult.Unchanged("wfp"),
        ]);
        registry.Record(
        [
            ReconcileResult.Unchanged("hosts"),
            ReconcileResult.Unchanged("apps"),
            ReconcileResult.Unchanged("wfp"),
        ]);

        Assert.Equal(["hosts", "apps", "wfp"], registry.Current().Select(layer => layer.Name));
    }

    [Fact]
    public void Current_MovesTheObservationTimeForwardWithEachPass()
    {
        var clock = new ServiceTestClock(Moment);
        var registry = new LayerStatusRegistry(clock);

        registry.Record([ReconcileResult.Unchanged("hosts")]);
        clock.Advance(TimeSpan.FromSeconds(15));
        registry.Record([ReconcileResult.Unchanged("hosts")]);

        Assert.Equal(Moment.AddSeconds(15), Assert.Single(registry.Current()).ObservedAt);
    }

    [Fact]
    public void Current_KeepsTheLastPassOfALayerThisPassDidNotReportOn()
    {
        // A pass can come back short; what was known about the other layers stays.
        var clock = new ServiceTestClock(Moment);
        var registry = new LayerStatusRegistry(clock);

        registry.Record([ReconcileResult.Skipped("wfp", "wfp.engine-unavailable"), ReconcileResult.Unchanged("hosts")]);
        clock.Advance(TimeSpan.FromSeconds(15));
        registry.Record([ReconcileResult.Unchanged("hosts")]);

        var wfp = registry.Current().Single(layer => layer.Name == "wfp");
        Assert.False(wfp.IsAvailable);
        Assert.Equal(Moment, wfp.ObservedAt);
    }

    [Fact]
    public void Record_AndCurrent_SurviveConcurrentCallers()
    {
        // The reconcile loop writes; one or more IPC connections read.
        var registry = new LayerStatusRegistry(new ServiceTestClock(Moment));

        Parallel.For(0, 400, i =>
        {
            registry.Record([ReconcileResult.Unchanged("hosts"), ReconcileResult.Skipped("wfp", $"pass {i}")]);
            Assert.All(registry.Current(), layer => Assert.NotNull(layer.Name));
        });

        Assert.Equal(2, registry.Current().Count);
    }
}
