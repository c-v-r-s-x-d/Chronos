using System.Runtime.Versioning;
using Chronos.Service.Setup;
using Microsoft.Win32;

namespace Chronos.Service.Tests;

/// <summary>The autostart entry uninstall reaches, against a throwaway key under <c>HKCU\Software\Chronos.Service.Tests</c>; the real Run key belongs to whoever runs the tests.</summary>
[SupportedOSPlatform("windows")]
public sealed class UserAutostartTests
{
    [Fact]
    public void TheEntryTheInterfaceWritesIsTheOneThisLooksFor()
    {
        // Both names are repeated in the interface's assembly, which the command-line tool must not link; the pair is pinned in the interface's suite and this checks this type's default.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", UserAutostart.RunKeyPath);
        Assert.Equal("Chronos", UserAutostart.EntryName);
    }

    [Fact]
    public void IsEnabled_IsFalseWhenNothingIsThere()
    {
        using var key = new ScratchKey();

        Assert.False(key.Autostart().IsEnabled());
    }

    [Fact]
    public void IsEnabled_IsTrueWhenTheEntryIsThere()
    {
        using var key = new ScratchKey();
        key.Write(@"C:\Program Files\Chronos\Chronos.App.exe");

        Assert.True(key.Autostart().IsEnabled());
    }

    [Fact]
    public void IsEnabled_IsFalseWhenThereIsNoKeyAtAll()
    {
        // A machine on which nothing has ever asked to start at logon has no Run key. Reading that
        // as an entry that is there would make uninstall report one it never removed.
        using var key = new ScratchKey(create: false);

        Assert.False(key.Autostart().IsEnabled());
    }

    [Fact]
    public void Disable_TakesTheEntryAway()
    {
        using var key = new ScratchKey();
        key.Write(@"C:\Program Files\Chronos\Chronos.App.exe");

        key.Autostart().Disable();

        Assert.Empty(key.Values());
    }

    [Fact]
    public void Disable_LeavesEverybodyElsesEntriesAlone()
    {
        // The Run key is shared with every other product on the machine. One value with one name
        // is the whole of what this may touch.
        using var key = new ScratchKey();
        key.Write(@"C:\Program Files\Chronos\Chronos.App.exe");
        key.Write(@"C:\Program Files\Something\Else.exe", name: "SomethingElse");

        key.Autostart().Disable();

        Assert.Equal(["SomethingElse"], key.Values());
    }

    [Fact]
    public void Disable_OnAKeyWithNoEntryIsNotAFailure()
    {
        // The wanted state is already the state. Raising here would make uninstall report a step
        // as failed, and answer 4, on every machine where the interface was never started.
        using var key = new ScratchKey();

        key.Autostart().Disable();

        Assert.Empty(key.Values());
    }

    [Fact]
    public void Disable_WithNoKeyAtAllIsNotAFailure()
    {
        using var key = new ScratchKey(create: false);

        key.Autostart().Disable();
    }

    /// <summary>A Run key of this test's own, taken away again when the test ends.</summary>
    private sealed class ScratchKey : IDisposable
    {
        private const string Root = @"Software\Chronos.Service.Tests";

        private readonly string _branch = $@"{Root}\{Guid.NewGuid():N}";

        public ScratchKey(bool create = true)
        {
            if (create)
            {
                Registry.CurrentUser.CreateSubKey($@"{_branch}\Run")?.Dispose();
            }
        }

        public UserAutostart Autostart() => new($@"{_branch}\Run", "Chronos");

        public string[] Values()
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{_branch}\Run", writable: false);

            return key?.GetValueNames() ?? [];
        }

        public void Write(string value, string name = "Chronos")
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{_branch}\Run", writable: true);
            key?.SetValue(name, value, RegistryValueKind.String);
        }

        /// <summary>The branch, and the root it hung from once nothing is left under it.</summary>
        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_branch, throwOnMissingSubKey: false);

            using var root = Registry.CurrentUser.OpenSubKey(Root, writable: false);
            if (root is not null && root.SubKeyCount == 0 && root.ValueCount == 0)
            {
                root.Dispose();
                try
                {
                    Registry.CurrentUser.DeleteSubKey(Root, throwOnMissingSubKey: false);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or IOException)
                {
                    // Another class just put its own key under the shared root; whoever finishes last removes it.
                }
            }
        }
    }
}
