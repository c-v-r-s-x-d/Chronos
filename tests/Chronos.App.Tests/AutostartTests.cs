using Chronos.App.Startup;
using Microsoft.Win32;

namespace Chronos.App.Tests;

/// <summary>
/// The autostart entry under HKCU. Every test but one uses a scratch key under
/// <c>HKCU\Software\Chronos.App.Tests</c>, so the real Run key is not written.
/// </summary>
public sealed class AutostartTests
{
    /// <summary>The Run key path, spelled out here so a change to the constant is visible.</summary>
    private const string TheRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void TheDefaultEntryIsTheOneUnderTheUsersOwnRunKey()
    {
        var autostart = new Autostart();

        Assert.Equal(TheRunKey, autostart.KeyPath);
        Assert.Equal("Chronos", autostart.Name);
    }

    [Fact]
    public void TheUninstallerLooksForTheEntryThisWrites()
    {
        // The CLI cannot link a graphical assembly, so uninstall repeats both names; this pins them against drift.
        var autostart = new Autostart();

        Assert.Equal(Service.Setup.UserAutostart.RunKeyPath, autostart.KeyPath);
        Assert.Equal(Service.Setup.UserAutostart.EntryName, autostart.Name);
    }

    [Fact]
    public void Enable_WritesTheEntry()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        Assert.False(autostart.IsEnabled);

        autostart.Enable(@"C:\Program Files\Chronos\Chronos.App.exe");

        Assert.True(autostart.IsEnabled);
        Assert.NotNull(autostart.Entry);
    }

    [Fact]
    public void Enable_RecordsThePathTheInterfaceWasStartedFrom()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        autostart.Enable(@"C:\Chronos\Chronos.App.exe");

        Assert.Contains(@"C:\Chronos\Chronos.App.exe", autostart.Entry!, StringComparison.Ordinal);
    }

    /// <summary>Quoted, because an unquoted command line stops at the space in Program Files.</summary>
    [Fact]
    public void Enable_QuotesThePathSoASpaceInItDoesNotSplitTheCommand()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        autostart.Enable(@"C:\Program Files\Chronos\Chronos.App.exe");

        Assert.Equal(@"""C:\Program Files\Chronos\Chronos.App.exe""", autostart.Entry);
    }

    [Fact]
    public void Enable_Twice_LeavesOneEntryAndNotTwo()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        autostart.Enable(@"C:\Chronos\Chronos.App.exe");
        autostart.Enable(@"C:\Chronos\Chronos.App.exe");

        Assert.Single(key.Values());
    }

    [Fact]
    public void Enable_AfterTheProductMoves_PointsAtWhereItIsNow()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        autostart.Enable(@"C:\Chronos\Chronos.App.exe");
        autostart.Enable(@"D:\Chronos\Chronos.App.exe");

        Assert.Single(key.Values());
        Assert.Contains(@"D:\Chronos", autostart.Entry!, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_WithNoEntry_WritesOneAndSaysSo()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        Assert.True(autostart.Ensure(@"C:\Chronos\Chronos.App.exe"));
        Assert.True(autostart.IsEnabled);
    }

    /// <summary>
    /// The MSI owns this entry on an installed machine. The interface repairs a missing entry and leaves
    /// an existing one as it is. The value written here is the package's, with the install folder resolved.
    /// </summary>
    [Fact]
    public void Ensure_WithTheEntryThePackageWrote_LeavesItExactlyAsItWas()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        key.Write(@"""C:\Program Files\Chronos\Chronos.App.exe""");

        Assert.False(autostart.Ensure(@"C:\Somewhere\Else\Chronos.App.exe"));
        Assert.Equal(@"""C:\Program Files\Chronos\Chronos.App.exe""", autostart.Entry);
        Assert.Single(key.Values());
    }

    [Fact]
    public void Disable_TakesTheEntryAway()
    {
        using var key = new ScratchKey();
        var autostart = key.Autostart();

        autostart.Enable(@"C:\Chronos\Chronos.App.exe");
        autostart.Disable();

        Assert.False(autostart.IsEnabled);
        Assert.Null(autostart.Entry);
        Assert.Empty(key.Values());
    }

    [Fact]
    public void Disable_WithNothingThere_IsQuiet()
    {
        using var key = new ScratchKey();

        key.Autostart().Disable();

        Assert.Empty(key.Values());
    }

    /// <summary>Reading a key that was never created answers "no entry".</summary>
    [Fact]
    public void Reading_AKeyThatDoesNotExist_SaysThereIsNoEntry()
    {
        var autostart = new Autostart(
            $@"Software\Chronos.App.Tests\{Guid.NewGuid():N}\Run",
            "Chronos");

        Assert.False(autostart.IsEnabled);
        Assert.Null(autostart.Entry);
    }

    [Fact]
    public void ThisExecutable_IsTheOneThisProcessIsRunning()
    {
        Assert.Equal(Environment.ProcessPath, Autostart.ThisExecutable);
    }

    /// <summary>
    /// The one test that touches the user's own Run key. It refuses to run if a Chronos entry exists,
    /// and asserts in a finally that the key is restored name for name and byte for byte.
    /// </summary>
    [RequiresNoChronosAutostart]
    public void TheRealRunKeyTakesTheEntryAndGivesItBack()
    {
        var before = RealRunKeyValues();
        Assert.DoesNotContain("Chronos", before.Keys);

        try
        {
            var autostart = new Autostart();
            autostart.Enable(@"C:\Chronos\Chronos.App.exe");

            Assert.True(autostart.IsEnabled);
            Assert.Equal(@"""C:\Chronos\Chronos.App.exe""", autostart.Entry);
            Assert.Contains("Chronos", RealRunKeyValues().Keys);

            autostart.Disable();
            Assert.False(autostart.IsEnabled);
        }
        finally
        {
            Restore(before);
        }

        var after = RealRunKeyValues();

        Assert.Equal(before.Count, after.Count);
        foreach (var (name, value) in before)
        {
            Assert.True(after.TryGetValue(name, out var still), $"{name} is gone from the Run key.");
            Assert.Equal(value, still);
        }

        Assert.DoesNotContain("Chronos", after.Keys);
    }

    private static Dictionary<string, string> RealRunKeyValues()
    {
        using var key = Registry.CurrentUser.OpenSubKey(TheRunKey, writable: false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (key is null)
        {
            return values;
        }

        foreach (var name in key.GetValueNames())
        {
            values[name] = key.GetValue(name)?.ToString() ?? string.Empty;
        }

        return values;
    }

    /// <summary>Puts the key back as found; only the one name this suite writes is removed.</summary>
    private static void Restore(IReadOnlyDictionary<string, string> before)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TheRunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (!before.ContainsKey("Chronos") && key.GetValue("Chronos") is not null)
        {
            key.DeleteValue("Chronos", throwOnMissingValue: false);
        }

        foreach (var (name, value) in before)
        {
            if (key.GetValue(name)?.ToString() != value)
            {
                key.SetValue(name, value, RegistryValueKind.String);
            }
        }
    }

    /// <summary>A Run key of this test's own, removed afterwards.</summary>
    private sealed class ScratchKey : IDisposable
    {
        private const string Root = @"Software\Chronos.App.Tests";

        private readonly string _branch = $@"{Root}\{Guid.NewGuid():N}";

        public ScratchKey() => Registry.CurrentUser.CreateSubKey($@"{_branch}\Run")?.Dispose();

        public Autostart Autostart() => new($@"{_branch}\Run", "Chronos");

        public string[] Values()
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{_branch}\Run", writable: false);

            return key?.GetValueNames() ?? [];
        }

        public void Write(string value)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{_branch}\Run", writable: true);
            key?.SetValue("Chronos", value, RegistryValueKind.String);
        }

        /// <summary>Removes the branch, and its root once empty.</summary>
        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_branch, throwOnMissingSubKey: false);

            using var root = Registry.CurrentUser.OpenSubKey(Root, writable: false);
            if (root is not null && root.SubKeyCount == 0 && root.ValueCount == 0)
            {
                root.Dispose();
                Registry.CurrentUser.DeleteSubKey(Root, throwOnMissingSubKey: false);
            }
        }
    }
}
