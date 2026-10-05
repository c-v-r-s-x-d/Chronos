using System.Security.AccessControl;
using System.Security.Principal;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger<ConfigStore> _logger = new();

    private ConfigStore CreateStore(out ChronosPaths paths)
    {
        paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();
        return new ConfigStore(paths, NullLogger<ConfigStore>.Instance);
    }

    private ConfigStore CreateLoggingStore(out ChronosPaths paths)
    {
        paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();
        return new ConfigStore(paths, _logger);
    }

    private LogLevel[] LevelsMentioning(string fragment) =>
    [
        .. _logger.Entries
            .Where(entry => entry.Message.Contains(fragment, StringComparison.Ordinal))
            .Select(entry => entry.Level),
    ];

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Load_ReturnsDefaultsWhenFileIsAbsent()
    {
        var store = CreateStore(out _);

        var config = store.Load();

        Assert.Empty(config.Sites);
        Assert.Empty(config.Apps);
        Assert.Equal((int)SessionLimits.DefaultCoolDown.TotalMinutes, config.CoolDownMinutes);
        Assert.Equal((int)SessionLimits.DefaultSessionDuration.TotalMinutes, config.DefaultSessionMinutes);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRules()
    {
        var store = CreateStore(out _);
        var saved = new ChronosConfig
        {
            Sites = [new SiteRuleDto("example.com", true)],
            Apps = [new AppRuleDto("FileName", "game.exe")],
            CoolDownMinutes = 5,
            DefaultSessionMinutes = 90,
        };

        store.Save(saved);
        var loaded = store.Load();

        Assert.Equal(saved.Sites, loaded.Sites);
        Assert.Equal(saved.Apps, loaded.Apps);
        Assert.Equal(5, loaded.CoolDownMinutes);
        Assert.Equal(90, loaded.DefaultSessionMinutes);
    }

    [Fact]
    public void Load_ClampsValuesOutsideTheAllowedRange()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 0, "defaultSessionMinutes": 100000 }
            """);

        var config = store.Load();

        Assert.Equal((int)SessionLimits.MinCoolDown.TotalMinutes, config.CoolDownMinutes);
        Assert.Equal((int)SessionLimits.MaxSessionDuration.TotalMinutes, config.DefaultSessionMinutes);
    }

    [Fact]
    public void Load_ReturnsDefaultsWhenFileIsCorrupt()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.ConfigFile, "{ this is not json");

        var config = store.Load();

        Assert.Empty(config.Sites);
        Assert.Equal((int)SessionLimits.DefaultCoolDown.TotalMinutes, config.CoolDownMinutes);
    }

    // Load runs every reconcile cycle; a cause that never goes away must not log the same
    // Information line every fifteen seconds.
    [Fact]
    public void Load_DropsAnUnchangedClampToDebugAfterTheFirstTime()
    {
        var store = CreateLoggingStore(out var paths);
        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 0 }
            """);

        store.Load();
        store.Load();
        store.Load();

        Assert.Equal(
            [LogLevel.Information, LogLevel.Debug, LogLevel.Debug],
            LevelsMentioning("cool-down"));
    }

    // A clamp of a different value is news again: the user edited the file and must see what it was corrected to.
    [Fact]
    public void Load_ReportsAClampAtItsOwnLevelAgainWhenTheConfiguredValueChanges()
    {
        var store = CreateLoggingStore(out var paths);
        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 0 }
            """);
        store.Load();
        store.Load();

        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 900 }
            """);
        store.Load();

        Assert.Equal(
            [LogLevel.Information, LogLevel.Debug, LogLevel.Information],
            LevelsMentioning("cool-down"));
    }

    // A cause that clears and comes back is news again, not stuck at Debug because it was once reported.
    [Fact]
    public void Load_ReportsAClampAtItsOwnLevelAgainAfterTheConditionClears()
    {
        var store = CreateLoggingStore(out var paths);
        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 0 }
            """);
        store.Load();

        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 5 }
            """);
        store.Load();

        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 0 }
            """);
        store.Load();

        Assert.Equal([LogLevel.Information, LogLevel.Information], LevelsMentioning("cool-down"));
    }

    // The read failure is written at Error: a file that cannot be parsed cannot parse itself on the next cycle either.
    [Fact]
    public void Load_DropsAnUnchangedReadFailureToDebugAfterTheFirstTime()
    {
        var store = CreateLoggingStore(out var paths);
        File.WriteAllText(paths.ConfigFile, "{ this is not json");

        store.Load();
        store.Load();
        store.Load();

        Assert.Equal([LogLevel.Error, LogLevel.Debug, LogLevel.Debug], LevelsMentioning("unreadable"));
    }

    [Fact]
    public void Load_ReportsAReadFailureAtItsOwnLevelAgainAfterTheFileIsRepaired()
    {
        var store = CreateLoggingStore(out var paths);
        File.WriteAllText(paths.ConfigFile, "{ this is not json");
        store.Load();
        store.Load();

        File.WriteAllText(paths.ConfigFile, "{ }");
        store.Load();

        File.WriteAllText(paths.ConfigFile, "{ still not json");
        store.Load();

        Assert.Equal([LogLevel.Error, LogLevel.Debug, LogLevel.Error], LevelsMentioning("unreadable"));
    }

    // File.ReadAllText throws UnauthorizedAccessException as readily as IOException (an ACL denying
    // this account the data is the ordinary cause), so it must not escape Load, which is on the
    // reconcile path.
    //
    // The Deny ACE induces the failure, and not every machine enforces one against the account that
    // set it (SYSTEM, or a volume without ACLs). The attribute proves the deny bites before this
    // runs, and skips where it does not.
    [RequiresEnforceableDenyAce]
    public void Load_FallsBackToDefaultsWhenTheFileCannotBeOpenedForWantOfPermission()
    {
        var store = CreateLoggingStore(out var paths);
        File.WriteAllText(paths.ConfigFile, """
            { "coolDownMinutes": 45 }
            """);

        // ReadData alone, not Read: Read carries ReadAttributes, so File.Exists would answer false and
        // Load would never try to open the file.
        var file = new FileInfo(paths.ConfigFile);
        var denial = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);

        var security = file.GetAccessControl();
        security.AddAccessRule(denial);
        file.SetAccessControl(security);

        try
        {
            var config = store.Load();

            Assert.Equal((int)SessionLimits.DefaultCoolDown.TotalMinutes, config.CoolDownMinutes);
            Assert.Equal([LogLevel.Error], LevelsMentioning("unreadable"));
        }
        finally
        {
            var restored = file.GetAccessControl();
            restored.RemoveAccessRule(denial);
            file.SetAccessControl(restored);
        }
    }

    [Fact]
    public void ToBlockList_MapsBothRuleKinds()
    {
        var config = new ChronosConfig
        {
            Sites = [new SiteRuleDto("example.com", true)],
            Apps = [new AppRuleDto("FullPath", @"C:\Games\game.exe")],
        };

        var list = config.ToBlockList();

        Assert.Contains(new SiteRule("example.com", includeSubdomains: true), list.Sites);
        Assert.Contains(new AppRule(AppMatchKind.FullPath, @"C:\Games\game.exe"), list.Apps);
    }

    [Fact]
    public void ToBlockList_SkipsRulesWithAnUnknownMatchKind()
    {
        var config = new ChronosConfig
        {
            Sites = [new SiteRuleDto("example.com", false)],
            Apps = [new AppRuleDto("Nonsense", "game.exe")],
        };

        var list = config.ToBlockList();

        Assert.Empty(list.Apps);
        Assert.Single(list.Sites);
    }

    [Fact]
    public void ToBlockList_SkipsAMatchKindGivenAsANumber()
    {
        // Enum.TryParse accepts any number, so "7" would become a kind no branch handles and a path
        // rule would silently widen into a name rule.
        var config = new ChronosConfig
        {
            Apps = [new AppRuleDto("7", @"C:\Games\game.exe")],
        };

        Assert.Empty(config.ToBlockList().Apps);
    }
}
