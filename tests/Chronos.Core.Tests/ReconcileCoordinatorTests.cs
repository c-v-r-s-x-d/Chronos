using Chronos.Core.Enforcement;

namespace Chronos.Core.Tests;

public sealed class ReconcileCoordinatorTests
{
    private sealed class StubEnforcer(string name) : IEnforcer
    {
        public string Name { get; } = name;

        public EnforcerAvailability Availability { get; set; } = EnforcerAvailability.Available;

        public Exception? ThrowOnProbe { get; set; }

        public Exception? ThrowOnReconcile { get; set; }

        public Exception? ThrowOnClear { get; set; }

        public bool UnchangedAfterFirstReconcile { get; set; }

        public int ReconcileCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public Task<EnforcerAvailability> ProbeAsync(CancellationToken ct)
        {
            if (ThrowOnProbe is not null)
            {
                throw ThrowOnProbe;
            }

            return Task.FromResult(Availability);
        }

        public Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
        {
            ReconcileCalls++;

            if (ThrowOnReconcile is not null)
            {
                throw ThrowOnReconcile;
            }

            if (UnchangedAfterFirstReconcile && ReconcileCalls > 1)
            {
                return Task.FromResult(ReconcileResult.Unchanged(Name));
            }

            return Task.FromResult(ReconcileResult.Changed(Name, applied: 1, removed: 0));
        }

        public Task ClearAsync(CancellationToken ct)
        {
            ClearCalls++;

            if (ThrowOnClear is not null)
            {
                throw ThrowOnClear;
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_InvokesEveryAvailableEnforcer()
    {
        var first = new StubEnforcer("first");
        var second = new StubEnforcer("second");
        var coordinator = new ReconcileCoordinator([first, second]);

        var results = await coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None);

        Assert.Equal(1, first.ReconcileCalls);
        Assert.Equal(1, second.ReconcileCalls);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RunAsync_SkipsUnavailableEnforcerAndReportsReason()
    {
        var unavailable = new StubEnforcer("dns")
        {
            Availability = EnforcerAvailability.Unavailable("dns.port-held"),
        };
        var coordinator = new ReconcileCoordinator([unavailable]);

        var results = await coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None);

        Assert.Equal(0, unavailable.ReconcileCalls);
        Assert.Equal(ReconcileOutcome.Skipped, results[0].Outcome);
        Assert.Equal("dns.port-held", results[0].Detail);
    }

    [Fact]
    public async Task RunAsync_ReportsACodeForALayerThatSaysItIsUnavailableWithoutSayingWhy()
    {
        var silent = new StubEnforcer("dns") { Availability = new EnforcerAvailability(false, null) };
        var coordinator = new ReconcileCoordinator([silent]);

        var results = await coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None);

        // The detail of a result travels to the interface. A word the coordinator made up would be
        // the service choosing what the user reads, in the language this file happens to be in.
        Assert.Equal("dns.unavailable", results[0].Detail);
    }

    [Fact]
    public async Task RunAsync_IsolatesFailureOfOneEnforcer()
    {
        var failure = new IOException("Access to the hosts file was denied.");
        var failing = new StubEnforcer("hosts") { ThrowOnReconcile = failure };
        var healthy = new StubEnforcer("wfp");
        var coordinator = new ReconcileCoordinator([failing, healthy]);

        var results = await coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, results[0].Outcome);

        // A code, not the message: the detail of a result reaches the interface, where a platform
        // sentence would arrive in whatever language the machine speaks. The exception goes on
        // the result, which is where the log takes it from.
        Assert.Equal("hosts.unhandled", results[0].Detail);
        Assert.Same(failure, results[0].Error);

        Assert.Equal(1, healthy.ReconcileCalls);
        Assert.Equal(ReconcileOutcome.Changed, results[1].Outcome);
    }

    [Fact]
    public async Task ClearAllAsync_ClearsEveryEnforcerEvenWhenUnavailable()
    {
        var unavailable = new StubEnforcer("dns")
        {
            Availability = EnforcerAvailability.Unavailable("unavailable"),
        };
        var available = new StubEnforcer("hosts");
        var coordinator = new ReconcileCoordinator([unavailable, available]);

        var results = await coordinator.ClearAllAsync(CancellationToken.None);

        Assert.Equal(1, unavailable.ClearCalls);
        Assert.Equal(1, available.ClearCalls);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(ReconcileOutcome.Changed, result.Outcome));
    }

    [Fact]
    public async Task ClearAllAsync_ReportsFailedResultButStillClearsOthers()
    {
        var failure = new IOException("Access is denied.");
        var failing = new StubEnforcer("hosts") { ThrowOnClear = failure };
        var healthy = new StubEnforcer("wfp");
        var coordinator = new ReconcileCoordinator([failing, healthy]);

        var results = await coordinator.ClearAllAsync(CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, results[0].Outcome);
        Assert.Equal("hosts.unhandled", results[0].Detail);
        Assert.Same(failure, results[0].Error);
        Assert.Equal(1, healthy.ClearCalls);
        Assert.Equal(ReconcileOutcome.Changed, results[1].Outcome);
    }

    [Fact]
    public async Task RunAsync_IsolatesProbeFailureOfOneEnforcer()
    {
        var failing = new StubEnforcer("dns")
        {
            ThrowOnProbe = new InvalidOperationException("Port 53 is bound by another process."),
        };
        var healthy = new StubEnforcer("hosts");
        var coordinator = new ReconcileCoordinator([failing, healthy]);

        var results = await coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, results[0].Outcome);
        Assert.Equal(1, healthy.ReconcileCalls);
    }

    [Fact]
    public async Task RunAsync_RunTwiceWithSamePlanReportsUnchangedOnSecondRun()
    {
        var enforcer = new StubEnforcer("hosts") { UnchangedAfterFirstReconcile = true };
        var coordinator = new ReconcileCoordinator([enforcer]);

        var first = await coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None);
        var second = await coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, first[0].Outcome);
        Assert.Equal(ReconcileOutcome.Unchanged, second[0].Outcome);
    }

    [Fact]
    public async Task RunAsync_DoesNotSwallowCancellation()
    {
        var enforcer = new StubEnforcer("wfp")
        {
            ThrowOnReconcile = new OperationCanceledException(),
        };
        var coordinator = new ReconcileCoordinator([enforcer]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => coordinator.RunAsync(EnforcementPlan.Empty, CancellationToken.None));
    }
}
