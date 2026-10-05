using System.Net;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class WfpEngineTests
{
    // TEST-NET-3, reserved for documentation and routed nowhere, so a filter on it can affect
    // nothing on this machine even for the moment it exists.
    private static readonly IPAddress Documentation = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress OtherDocumentation = IPAddress.Parse("203.0.113.2");

    // An identity of this suite's own, fixed so a run killed mid-test leaves objects a later run recognises.
    private static readonly WfpObjectIdentity TestIdentity = new(
        new Guid("2b9f1c6e-77a4-4de1-9d0b-3a6e8c5f21b7"),
        new Guid("5e3c07a8-1b4f-4a92-8c6d-7f21b0e94d33"),
        "Chronos test suite",
        "Chronos test suite site block");

    /// <summary>Never persistent: a test that leaves a filter behind survives a reboot. Never the service's own identity either.</summary>
    private static WfpEngine Create() => new(persistent: false, NullLogger<WfpEngine>.Instance, TestIdentity);

    // ListOwnFilters and RemoveEverything select by provider key only. A dynamic session governs how long its filters survive, not what it can see:
    // under the service's own key, an administrator run with an active Chronos session would list and delete that session's persistent filters.
    [Fact]
    public void TheseTestsFileUnderAnIdentityOfTheirOwn()
    {
        Assert.NotEqual(WfpObjectIdentity.Production.ProviderKey, TestIdentity.ProviderKey);
        Assert.NotEqual(WfpObjectIdentity.Production.SubLayerKey, TestIdentity.SubLayerKey);
    }

    [RequiresElevation]
    public void OpenAndDispose_LeaveNothingBehind()
    {
        using (var engine = Create())
        {
            engine.Open();
            engine.EnsureObjects();
            engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos test");
            Assert.Single(engine.ListOwnFilters());
        }

        // A fresh dynamic session cannot see what the previous one added.
        using var next = Create();
        next.Open();
        next.EnsureObjects();
        Assert.Empty(next.ListOwnFilters());
    }

    [RequiresElevation]
    public void EnsureObjects_IsIdempotent()
    {
        using var engine = Create();
        engine.Open();

        engine.EnsureObjects();
        engine.EnsureObjects();

        Assert.Empty(engine.ListOwnFilters());
    }

    [RequiresElevation]
    public void ListOwnFilters_ReturnsOnlyWhatThisProviderAdded()
    {
        using var engine = Create();
        engine.Open();
        engine.EnsureObjects();

        var first = engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos test one");
        var second = engine.AddBlock(OtherDocumentation, WfpProtocol.QuicUdp443, "chronos test two");

        var listed = engine.ListOwnFilters();
        Assert.Equal(2, listed.Count);
        Assert.Contains(new WfpFilterInfo(first, "chronos test one"), listed);
        Assert.Contains(new WfpFilterInfo(second, "chronos test two"), listed);
    }

    [RequiresElevation]
    public void RemoveFilters_DeletesOnlyTheOnesNamed()
    {
        using var engine = Create();
        engine.Open();
        engine.EnsureObjects();
        var kept = engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos kept");
        var dropped = engine.AddBlock(OtherDocumentation, WfpProtocol.AnyTransport, "chronos dropped");

        engine.RemoveFilters([dropped]);

        Assert.Equal([new WfpFilterInfo(kept, "chronos kept")], engine.ListOwnFilters());
    }

    [RequiresElevation]
    public void RemoveEverything_ClearsThisProvidersFilters()
    {
        using var engine = Create();
        engine.Open();
        engine.EnsureObjects();
        engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos test");
        engine.AddBlock(OtherDocumentation, WfpProtocol.AnyTransport, "chronos test");

        var removed = engine.RemoveEverything();

        Assert.Equal(2, removed.Filters);
        Assert.Empty(engine.ListOwnFilters());
    }

    // Every change comes off. The provider and sub-layer are created persistent, so leaving them would strand two Chronos-named objects no second `chronos clean` removes.
    [RequiresElevation]
    public void RemoveEverything_TakesTheProviderAndSubLayerWithIt()
    {
        using var engine = Create();
        engine.Open();
        engine.EnsureObjects();
        engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos test");

        var removal = engine.RemoveEverything();
        Assert.Equal(1, removal.Filters);

        // Reported, so `chronos clean` can tell that a machine with no filters still carried two platform objects.
        Assert.True(removal.ProviderRemoved);
        Assert.True(removal.SubLayerRemoved);

        // The provider really is gone, taken from the platform rather than assumed: a filter
        // naming a provider that does not exist is rejected with FWP_E_PROVIDER_NOT_FOUND.
        var rejected = Assert.Throws<InvalidOperationException>(
            () => engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos after clean"));
        Assert.Contains("0x80320005", rejected.Message, StringComparison.Ordinal);

        // ...and EnsureObjects puts both back, so the next session still works.
        engine.EnsureObjects();
        engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos after clean");
        Assert.Single(engine.ListOwnFilters());
    }

    // A second `chronos clean`, and a clean machine, must both succeed rather than fault on an
    // object that is already absent.
    [RequiresElevation]
    public void RemoveEverything_SucceedsWhenThereIsNothingLeftToRemove()
    {
        using var engine = Create();
        engine.Open();
        engine.EnsureObjects();

        var first = engine.RemoveEverything();
        var second = engine.RemoveEverything();

        Assert.Equal(0, first.Filters);
        Assert.True(first.AnythingRemoved); // the two objects EnsureObjects just created

        // And a machine already clean says so rather than claiming a removal: this is the run in
        // which `chronos clean` must print nothing about a provider.
        Assert.False(second.AnythingRemoved);
    }

    [RequiresElevation]
    public void AddBlock_AcceptsBothAddressFamilies()
    {
        using var engine = Create();
        engine.Open();
        engine.EnsureObjects();

        engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos v4");
        engine.AddBlock(IPAddress.Parse("2001:db8::1"), WfpProtocol.AnyTransport, "chronos v6");

        Assert.Equal(2, engine.ListOwnFilters().Count);
    }

    // Whether the clear path has to create the provider before it can enumerate: the enumeration
    // template is keyed by the provider GUID, not by an object this session owns. If it needs no
    // provider, a disabled layer never has to create one.
    [RequiresElevation]
    public void ListingDoesNotNeedTheProviderToExist()
    {
        using var engine = Create();
        engine.Open();

        // The premise, taken from the platform: with no provider object, a filter naming it is rejected with FWP_E_PROVIDER_NOT_FOUND.
        // An empty enumeration proves nothing if the provider existed, which the suite's own identity (created only in a dynamic session) rules out.
        // It also pins why the clear path cannot miss a filter.
        var rejected = Assert.Throws<InvalidOperationException>(
            () => engine.AddBlock(Documentation, WfpProtocol.AnyTransport, "chronos premise"));
        Assert.Contains("0x80320005", rejected.Message, StringComparison.Ordinal);

        Assert.Empty(engine.ListOwnFilters());
        Assert.False(engine.RemoveEverything().AnythingRemoved);
    }

    [Fact]
    public void Dispose_IsSafeWhenTheEngineWasNeverOpened()
    {
        using var engine = Create();
    }
}
