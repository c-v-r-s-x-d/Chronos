using Chronos.Core.Rules;

namespace Chronos.Core.Tests;

public sealed class BlockListTests
{
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);
    private static readonly AppRule App = new(AppMatchKind.FileName, "game.exe");

    [Fact]
    public void Empty_HasNoRules()
    {
        Assert.True(BlockList.Empty.IsEmpty);
        Assert.Empty(BlockList.Empty.Sites);
        Assert.Empty(BlockList.Empty.Apps);
    }

    [Fact]
    public void WithSite_ReturnsNewInstanceAndLeavesOriginalUnchanged()
    {
        var original = BlockList.Empty;

        var extended = original.WithSite(Site);

        Assert.Empty(original.Sites);
        Assert.Single(extended.Sites);
        Assert.Contains(Site, extended.Sites);
    }

    [Fact]
    public void WithApp_ReturnsNewInstanceAndLeavesOriginalUnchanged()
    {
        var original = BlockList.Empty;

        var extended = original.WithApp(App);

        Assert.Empty(original.Apps);
        Assert.Single(extended.Apps);
        Assert.Contains(App, extended.Apps);
    }

    [Fact]
    public void WithSite_DeduplicatesEqualRules()
    {
        var list = BlockList.Empty
            .WithSite(new SiteRule("example.com", includeSubdomains: true))
            .WithSite(new SiteRule("EXAMPLE.COM.", includeSubdomains: true));

        Assert.Single(list.Sites);
    }

    [Fact]
    public void WithoutSite_RemovesMatchingRule()
    {
        var list = BlockList.Empty.WithSite(Site);

        var reduced = list.WithoutSite(new SiteRule("example.com", includeSubdomains: true));

        Assert.Empty(reduced.Sites);
        Assert.Single(list.Sites);
    }

    [Fact]
    public void WithoutApp_RemovesMatchingRule()
    {
        var list = BlockList.Empty.WithApp(App);

        var reduced = list.WithoutApp(new AppRule(AppMatchKind.FileName, "game.exe"));

        Assert.Empty(reduced.Apps);
    }

    [Fact]
    public void AppRule_RejectsEmptyValue()
    {
        Assert.Throws<ArgumentException>(() => new AppRule(AppMatchKind.FullPath, "  "));
    }

    [Fact]
    public void AppRule_DistinguishesMatchKinds()
    {
        var byPath = new AppRule(AppMatchKind.FullPath, "game.exe");
        var byName = new AppRule(AppMatchKind.FileName, "game.exe");

        Assert.NotEqual(byPath, byName);
    }

    [Fact]
    public void Sites_CannotBeMutatedThroughACast()
    {
        var list = BlockList.Empty.WithSite(Site);

        var mutableView = (ICollection<SiteRule>)list.Sites;

        Assert.Throws<NotSupportedException>(() => mutableView.Add(new SiteRule("evil.com", includeSubdomains: false)));
        Assert.Single(list.Sites);
    }
}
