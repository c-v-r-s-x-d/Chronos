using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Service.Configuration;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class StateStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));

    private StateStore CreateStore(out ChronosPaths paths)
    {
        paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();
        return new StateStore(paths, NullLogger<StateStore>.Instance);
    }

    private static BlockSession SampleSession(UnlockRequest? unlock = null) => new()
    {
        Id = Guid.NewGuid(),
        StartedAt = Start,
        EndsAt = Start.AddHours(2),
        CoolDown = TimeSpan.FromMinutes(5),
        Rules = BlockList.Empty
            .WithSite(new SiteRule("example.com", includeSubdomains: true))
            .WithApp(new AppRule(AppMatchKind.FileName, "game.exe")),
        Unlock = unlock,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Load_ReturnsNullWhenNothingWasSaved()
    {
        var store = CreateStore(out _);

        Assert.Null(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = CreateStore(out _);
        var session = SampleSession();

        store.Save(session);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded!.Id);
        Assert.Equal(session.StartedAt, loaded.StartedAt);
        Assert.Equal(session.EndsAt, loaded.EndsAt);
        Assert.Equal(session.CoolDown, loaded.CoolDown);
        Assert.Contains(new SiteRule("example.com", includeSubdomains: true), loaded.Rules.Sites);
        Assert.Contains(new AppRule(AppMatchKind.FileName, "game.exe"), loaded.Rules.Apps);
        Assert.Null(loaded.Unlock);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAPendingUnlock()
    {
        var store = CreateStore(out _);
        var unlock = new UnlockRequest(Start.AddHours(1), Start.AddHours(1).AddMinutes(5));

        store.Save(SampleSession(unlock));
        var loaded = store.Load();

        Assert.Equal(unlock.RequestedAt, loaded!.Unlock!.RequestedAt);
        Assert.Equal(unlock.EffectiveAt, loaded.Unlock.EffectiveAt);
    }

    [Fact]
    public void Load_ReturnsNullAndReportsWhenTheFileIsCorrupt()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.StateFile, "{ broken");

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ReturnsNullWhenTheFileParsesButCarriesNoSession()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.StateFile, "{}");

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ReturnsNullWhenOnlySomeFieldsArePresent()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.StateFile, """
            { "endsAt": "2030-01-01T00:00:00+00:00" }
            """);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_SkipsMalformedRulesAndKeepsTheValidOnes()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.StateFile, """
            {
              "id": "8a1f4e2c-3b7d-4c9a-8e5f-1d2c3b4a5e6f",
              "startedAt": "2026-08-16T12:00:00+00:00",
              "endsAt": "2026-08-16T14:00:00+00:00",
              "coolDownMinutes": 5,
              "sites": [ { "domain": "example.com", "includeSubdomains": true }, { "domain": "  ", "includeSubdomains": false } ],
              "apps": [ { "matchKind": "FileName", "value": "game.exe" }, { "matchKind": "Nonsense", "value": "other.exe" } ]
            }
            """);

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Rules.Sites);
        Assert.Single(loaded.Rules.Apps);
        Assert.Contains(loaded.Rules.Apps, app => app.Value == "game.exe");
    }

    [Fact]
    public void Load_DeletesACorruptFileSoItDoesNotFailForever()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.StateFile, "{ broken");

        Assert.Null(store.Load());

        Assert.False(File.Exists(paths.StateFile));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_SurvivesAnUnusableFileThatCannotBeDeleted()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.StateFile, "{ broken");
        File.SetAttributes(paths.StateFile, FileAttributes.ReadOnly);

        try
        {
            // Load runs from the host's service registration: a throw here would stop the service
            // from starting at all, which is a far worse outcome than a leftover file.
            Assert.Null(store.Load());
        }
        finally
        {
            File.SetAttributes(paths.StateFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Clear_RemovesTheFile()
    {
        var store = CreateStore(out var paths);
        store.Save(SampleSession());

        store.Clear();

        Assert.False(File.Exists(paths.StateFile));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Clear_IsSafeWhenNothingWasSaved()
    {
        var store = CreateStore(out _);

        store.Clear();
    }

    [Fact]
    public void Save_LeavesNoTemporaryFilesBehind()
    {
        var store = CreateStore(out var paths);

        store.Save(SampleSession());

        var files = Directory.GetFiles(paths.DataDirectory);
        Assert.Single(files);
        Assert.Equal(paths.StateFile, files[0]);
    }
}
