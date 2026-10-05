using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Chronos.Service.Tests;

/// <summary>
/// The backup of the interface settings and the two places it is kept. The store is checked against
/// a fake mirror (the real one writes under HKLM); the real mirror is checked on its own against a
/// key of this test's own under HKCU, since which value and key it writes is what a fake cannot say.
/// Backups here carry values written out in full, not taken from the code under test.
/// </summary>
public sealed class DnsBackupStoreTests : IDisposable
{
    private static readonly DateTimeOffset SavedAt = new(2026, 9, 3, 10, 30, 0, TimeSpan.FromHours(3));

    /// <summary>A copy written out by hand, so a test can tell which source answered.</summary>
    private const string RegistryCopy =
        """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[{"guid":"{bbb}","index":9,"name":"Ethernet 2","isDhcp":true,"servers":["1.1.1.1"]}]}""";

    /// <summary>The same document, of a session that ended before the one the file copy is of.</summary>
    private const string OlderRegistryCopy =
        """{"savedAt":"2026-09-01T00:00:00+03:00","interfaces":[{"guid":"{bbb}","index":9,"name":"Ethernet 2","isDhcp":true,"servers":["1.1.1.1"]}]}""";

    /// <summary>And of a session that ended after it.</summary>
    private const string NewerRegistryCopy =
        """{"savedAt":"2026-09-05T00:00:00+03:00","interfaces":[{"guid":"{bbb}","index":9,"name":"Ethernet 2","isDhcp":true,"servers":["1.1.1.1"]}]}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingMirror _mirror = new();
    private readonly CapturingLogger<DnsBackupStore> _logger = new();
    private readonly ChronosPaths _paths;

    public DnsBackupStoreTests()
    {
        _paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        _paths.EnsureDataDirectoryExists();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DnsBackupStore Make(IBackupMirror? mirror = null) => new(_paths, mirror ?? _mirror, _logger);

    private static DnsBackup Backup(string guid, params string[] servers) =>
        new(SavedAt, [new InterfaceDnsState(guid, 7, "Wi-Fi", IsDhcp: false, servers)]);

    private static DnsBackup Both(DateTimeOffset at) => new(
        at,
        [
            new InterfaceDnsState("{aaa}", 7, "Wi-Fi", IsDhcp: false, ["8.8.8.8"]),
            new InterfaceDnsState("{bbb}", 9, "Ethernet", IsDhcp: false, ["1.1.1.1"]),
        ]);

    /// <summary>Puts something in the way of the file, so that writing and deleting it fail.</summary>
    private void BlockTheFile() => Directory.CreateDirectory(_paths.DnsBackupFile);

    [Fact]
    public void Save_WritesBothSources()
    {
        var store = Make();

        store.Save(Backup("{aaa}", "8.8.8.8"));

        Assert.True(File.Exists(_paths.DnsBackupFile));
        Assert.NotNull(_mirror.Written);
    }

    [Fact]
    public void Save_PutsTheSameDocumentInBothPlaces()
    {
        var store = Make();

        store.Save(Backup("{aaa}", "8.8.8.8"));

        // Character for character: two spellings of one backup can disagree.
        Assert.Equal(File.ReadAllText(_paths.DnsBackupFile), _mirror.Written);

        // And in the shape every other document of this product is written in.
        Assert.Contains("\"isDhcp\"", _mirror.Written, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveThenLoad_CarriesEveryFieldOfEveryInterface()
    {
        var store = Make();

        store.Save(new DnsBackup(
            SavedAt,
            [
                new InterfaceDnsState("{11111111-2222-3333-4444-555555555555}", 22, "Wi-Fi", IsDhcp: false, ["8.8.8.8", "8.8.4.4"]),
                new InterfaceDnsState("{66666666-7777-8888-9999-000000000000}", 12, "Ethernet", IsDhcp: true, ["172.20.10.1"]),
            ]));

        var loaded = store.Load()!;

        // Written out rather than compared against the saved object, which a store handing back its input would pass.
        Assert.Equal(SavedAt, loaded.SavedAt);
        Assert.Equal(TimeSpan.FromHours(3), loaded.SavedAt.Offset);
        Assert.Equal(2, loaded.Interfaces.Count);

        Assert.Equal("{11111111-2222-3333-4444-555555555555}", loaded.Interfaces[0].Guid);
        Assert.Equal(22, loaded.Interfaces[0].Index);
        Assert.Equal("Wi-Fi", loaded.Interfaces[0].Name);
        Assert.False(loaded.Interfaces[0].IsDhcp);
        Assert.Equal(["8.8.8.8", "8.8.4.4"], loaded.Interfaces[0].Servers);

        // The second differs in every field, so an interface reported twice is not a list that passes.
        Assert.Equal("{66666666-7777-8888-9999-000000000000}", loaded.Interfaces[1].Guid);
        Assert.Equal(12, loaded.Interfaces[1].Index);
        Assert.Equal("Ethernet", loaded.Interfaces[1].Name);
        Assert.True(loaded.Interfaces[1].IsDhcp);
        Assert.Equal(["172.20.10.1"], loaded.Interfaces[1].Servers);
    }

    [Fact]
    public void Load_RestoresEveryFieldFromTheRegistryAlone()
    {
        // The file is gone, so this is the registry document being read; it must carry the same machine as the file one.
        var store = Make();
        store.Save(new DnsBackup(
            SavedAt, [new InterfaceDnsState("{aaa}", 22, "Wi-Fi", IsDhcp: true, ["1.1.1.1", "9.9.9.9"])]));
        File.Delete(_paths.DnsBackupFile);

        var one = Assert.Single(store.Load()!.Interfaces);

        Assert.Equal("{aaa}", one.Guid);
        Assert.Equal(22, one.Index);
        Assert.Equal("Wi-Fi", one.Name);
        Assert.True(one.IsDhcp);
        Assert.Equal(["1.1.1.1", "9.9.9.9"], one.Servers);
    }

    [Fact]
    public void Load_ReturnsNullWhenNothingWasEverSaved()
    {
        Assert.Null(Make().Load());
    }

    [Fact]
    public void Load_AnswersFromTheRegistryWhenTheFileHasTurnedIntoRubbish()
    {
        // Two different machines in the two places, so what comes back says which source answered.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = RegistryCopy;
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");

        var one = Assert.Single(store.Load()!.Interfaces);

        Assert.Equal("{bbb}", one.Guid);
        Assert.Equal(["1.1.1.1"], one.Servers);
    }

    [Fact]
    public void Load_AnswersFromTheRegistryWhenTheFileIsGone()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = RegistryCopy;
        File.Delete(_paths.DnsBackupFile);

        var one = Assert.Single(store.Load()!.Interfaces);

        Assert.Equal("{bbb}", one.Guid);
        Assert.Equal(["1.1.1.1"], one.Servers);
    }

    [Fact]
    public void Load_AnswersFromTheRegistryWhenTheFileIsThereAndEmpty()
    {
        // A file of nothing is what an interrupted write leaves behind.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = RegistryCopy;
        File.WriteAllText(_paths.DnsBackupFile, string.Empty);

        Assert.Equal(["1.1.1.1"], store.Load()!.Interfaces[0].Servers);
    }

    [Fact]
    public void Load_AnswersFromTheRegistryWhenTheFileIsThereAndCannotBeRead()
    {
        // Held open by an antivirus or a backup agent: the file is on disk and nothing can open it. On a
        // real machine that is what "the state file is damaged" most often looks like.
        _mirror.Written = RegistryCopy;
        File.WriteAllText(
            _paths.DnsBackupFile,
            """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[{"guid":"{ccc}","index":3,"name":"Wi-Fi","isDhcp":false,"servers":["8.8.8.8"]}]}""");

        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.None);

        // The file document would have won on a tie, so only the read failing puts the registry's in its place.
        var one = Assert.Single(Make().Load()!.Interfaces);

        Assert.Equal("{bbb}", one.Guid);
        Assert.Contains(
            (LogLevel.Warning, "The DNS backup file could not be read; the registry copy is the one left."),
            _logger.Entries);
    }

    [Fact]
    public void Load_FallsBackToTheRegistryWhenTheFileNamesNoInterfaces()
    {
        // Valid JSON that restores nothing is not a copy; the other source is asked instead.
        _mirror.Written = RegistryCopy;
        File.WriteAllText(_paths.DnsBackupFile, """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[]}""");

        Assert.Equal(["1.1.1.1"], Make().Load()!.Interfaces[0].Servers);
    }

    [Fact]
    public void Load_FallsBackToTheRegistryWhenTheFileCarriesNoListAtAll()
    {
        // A different document: this one parses into a backup whose list is null, and asking such a list
        // for its length would turn a restore into an exception.
        _mirror.Written = RegistryCopy;
        File.WriteAllText(_paths.DnsBackupFile, """{"savedAt":"2026-09-03T10:30:00+03:00"}""");

        Assert.Equal(["1.1.1.1"], Make().Load()!.Interfaces[0].Servers);
    }

    [Fact]
    public void Load_AnswersFromTheFileWhenTheRegistryCopyHasTurnedIntoRubbish()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = "not json either";

        var one = Assert.Single(store.Load()!.Interfaces);

        Assert.Equal("{aaa}", one.Guid);
        Assert.Equal(["8.8.8.8"], one.Servers);
    }

    [Fact]
    public void Load_TakesTheFresherCopyWhenTheRegistryHoldsIt()
    {
        // What a session leaves behind when the file half of the save failed: the document on disk is the
        // machine's state from last time. Restoring from it would hand back a replaced router, a corporate
        // resolver the laptop has left, or the 127.0.0.1 a half-applied run wrote.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = NewerRegistryCopy;

        var one = Assert.Single(store.Load()!.Interfaces);

        Assert.Equal("{bbb}", one.Guid);
        Assert.Equal(["1.1.1.1"], one.Servers);
    }

    [Fact]
    public void Load_TakesTheFresherCopyWhenTheFileHoldsIt()
    {
        // The same rule the other way round, so "the file wins" is not what is being tested.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = OlderRegistryCopy;

        var one = Assert.Single(store.Load()!.Interfaces);

        Assert.Equal("{aaa}", one.Guid);
        Assert.Equal(["8.8.8.8"], one.Servers);
    }

    [Fact]
    public void Load_TakesTheFileCopyWhenBothWereSavedAtTheSameMoment()
    {
        // One save reached both places, so the two documents are the same and there is nothing to choose.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = RegistryCopy;

        var one = Assert.Single(store.Load()!.Interfaces);

        Assert.Equal("{aaa}", one.Guid);
        Assert.Equal(["8.8.8.8"], one.Servers);
    }

    [Fact]
    public void Load_TakesTheOneUsableCopyHoweverOldItIs()
    {
        // Old is not unusable: a machine whose last session ran years ago still has the settings it took
        // over, and nowhere else they are written down.
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");
        _mirror.Written =
            """{"savedAt":"2019-01-01T00:00:00+00:00","interfaces":[{"guid":"{ccc}","index":4,"name":"Wi-Fi","isDhcp":false,"servers":["9.9.9.9"]}]}""";

        var one = Assert.Single(Make().Load()!.Interfaces);

        Assert.Equal("{ccc}", one.Guid);
        Assert.Equal(["9.9.9.9"], one.Servers);
    }

    [Fact]
    public void Load_TreatsACopyWithNoMomentOnItAsTheOlderOneAndStillTakesItAlone()
    {
        // A document whose moment did not parse, or that predates there being one, loses to any copy that
        // carries a moment and is still a copy when it is the only one.
        const string NoMoment =
            """{"interfaces":[{"guid":"{ddd}","index":4,"name":"Wi-Fi","isDhcp":false,"servers":["9.9.9.9"]}]}""";

        File.WriteAllText(_paths.DnsBackupFile, NoMoment);
        _mirror.Written = OlderRegistryCopy;

        Assert.Equal("{bbb}", Assert.Single(Make().Load()!.Interfaces).Guid);

        _mirror.Written = null;

        Assert.Equal("{ddd}", Assert.Single(Make().Load()!.Interfaces).Guid);
    }

    [Fact]
    public void Load_AsksTheRegistryExactlyOnceEvenWhenTheFileAnswers()
    {
        // Both sources are read on every Load, each once. Asking the registry again per question would make
        // a restore wait on the hive once per interface.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Reads = 0;

        store.Load();

        Assert.Equal(1, _mirror.Reads);
    }

    [Fact]
    public void Load_GoesToTheRegistryOnceWhenTheFileDoesNot()
    {
        // With nothing on disk the registry is still read once, not once per attempt.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        File.Delete(_paths.DnsBackupFile);
        _mirror.Reads = 0;

        store.Load();

        Assert.Equal(1, _mirror.Reads);
    }

    [Fact]
    public void Load_IsNullWhenBothSourcesAreCorrupt()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");
        _mirror.Written = "not json either";

        // Null, not an exception: the caller has to decide what to do about a machine with no copy.
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_TreatsABackupWithNoInterfacesAsNoBackup()
    {
        var store = Make();

        store.Save(new DnsBackup(SavedAt, []));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_TakesABackupNamingOneInterface()
    {
        // The other side of that boundary: one interface is a copy something can be restored from.
        var store = Make();

        store.Save(Backup("{aaa}", "8.8.8.8"));

        Assert.NotNull(store.Load());
    }

    [Fact]
    public void Load_IsNullWhenTheRegistryCannotBeReadAtAll()
    {
        // No file, and a registry that refuses. Still an answer, not an exception.
        Assert.Null(Make(new ThrowingMirror()).Load());
    }

    [Fact]
    public void Load_IsNullWhenTheRegistryHasNoRightsToTheKey()
    {
        // SecurityException is what the registry API throws when the account lacks rights to the key.
        // It differs from UnauthorizedAccessException, and FromRegistry's catch filter must name it too.
        Assert.Null(Make(new ThrowingMirror(() => new SecurityException())).Load());
    }

    [Fact]
    public void Load_NamesTheSourceWhoseDocumentItCouldNotRead()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");

        store.Load();

        // The restore worked from the other source, so without this line nothing would say that one place
        // holds rubbish. The name of the place matters: an administrator sent to the registry over a
        // broken file repairs the wrong half.
        Assert.Contains((LogLevel.Warning, "The DNS backup in the file could not be read."), _logger.Entries);
        Assert.DoesNotContain(
            _logger.Entries, entry => entry.Message.Contains("registry", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_NamesTheRegistryWhenTheRegistryIsTheSourceItCouldNotRead()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = "not json either";

        store.Load();

        Assert.Contains((LogLevel.Warning, "The DNS backup in the registry could not be read."), _logger.Entries);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Message.Contains("file", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_SaysWhenASourceHoldsADocumentNothingCanBeRestoredFrom()
    {
        // Not the same finding as a document that would not parse: this is a well-formed backup of nothing.
        _mirror.Written = RegistryCopy;
        File.WriteAllText(_paths.DnsBackupFile, """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[]}""");

        Make().Load();

        Assert.Contains((LogLevel.Warning, "The DNS backup in the file names no interfaces."), _logger.Entries);
    }

    [Fact]
    public void Load_SaysNothingAboutAMachineThatSimplyHasNoCopy()
    {
        // Load runs on every pass that decides whether to restore. A machine before its first session
        // has no copy in either place, and a line about it each time would bury the real findings.
        Make().Load();

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void Sources_ReadsTheTwoPlacesApart()
    {
        // diag says which place holds a copy; the fresher of the two would hide the other.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = RegistryCopy;

        var sources = store.Sources();

        Assert.Equal("{aaa}", Assert.Single(sources.File!.Interfaces).Guid);
        Assert.Equal("{bbb}", Assert.Single(sources.Registry!.Interfaces).Guid);
    }

    [Fact]
    public void Sources_HasNoFileCopyWhenTheFileIsRubbish()
    {
        var store = Make();
        _mirror.Written = RegistryCopy;
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");

        var sources = store.Sources();

        Assert.Null(sources.File);
        Assert.NotNull(sources.Registry);
    }

    [Fact]
    public void Sources_HasNoRegistryCopyWhenTheRegistryHoldsNone()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = null;

        var sources = store.Sources();

        Assert.NotNull(sources.File);
        Assert.Null(sources.Registry);
    }

    [Fact]
    public void Sources_ChangesNeitherPlace()
    {
        // Read by diag, which must leave the machine as it found it.
        var store = Make();
        _mirror.Written = RegistryCopy;
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");

        store.Sources();

        Assert.Equal("{ this is not json", File.ReadAllText(_paths.DnsBackupFile));
        Assert.Equal(RegistryCopy, _mirror.Written);
        Assert.Equal(0, _mirror.Clears);
    }

    [Fact]
    public void Sources_FresherIsWhatLoadWouldAnswer()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = NewerRegistryCopy;

        Assert.Equal("{bbb}", Assert.Single(store.Sources().Fresher!.Interfaces).Guid);
    }

    [Fact]
    public void HoldsACopy_IsTrueForAUsableCopyInEitherPlace()
    {
        var store = Make();
        _mirror.Written = RegistryCopy;
        Assert.True(store.HoldsACopy());

        _mirror.Written = null;
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Written = null;
        Assert.True(store.HoldsACopy());
    }

    [Fact]
    public void HoldsACopy_IsFalseForRubbishAndSaysNothing()
    {
        // Asked every 15 s while the layer holds interfaces.
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");

        Assert.False(Make(new ThrowingMirror()).HoldsACopy());
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void HoldsACopy_DoesNotAskTheRegistryWhenTheFileAnswers()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        _mirror.Reads = 0;

        Assert.True(store.HoldsACopy());
        Assert.Equal(0, _mirror.Reads);
    }

    [Fact]
    public void HoldsACopyOf_IsFalseForRubbishAndSaysNothing()
    {
        // The form the service asks on every held pass.
        File.WriteAllText(_paths.DnsBackupFile, "{ this is not json");

        Assert.False(Make(new ThrowingMirror()).HoldsACopyOf(["{aaa}"]));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void HoldsACopyOf_AnUnreadableFileDoesNotQuietTheNextLoad()
    {
        Make().Save(Backup("{aaa}", "8.8.8.8"));
        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var store = Make();

        store.HoldsACopyOf(["{aaa}"]);
        _logger.Clear();
        store.Load();

        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("file could not be read"));
    }

    [Fact]
    public void HoldsACopyOf_AnUnreadableRegistryDoesNotQuietTheNextLoad()
    {
        var store = Make(new ThrowingMirror());

        store.HoldsACopyOf(["{aaa}"]);
        _logger.Clear();
        store.Load();

        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("registry could not be read"));
    }

    [Fact]
    public void HoldsACopyOf_JudgesTheFileWhenBothPlacesHoldTheSameMoment()
    {
        // A prune that reached only the file. Load takes the file on a tie, so the wider registry copy does not count.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        var pruned = File.ReadAllText(_paths.DnsBackupFile);
        store.Save(Both(SavedAt));
        File.WriteAllText(_paths.DnsBackupFile, pruned);

        Assert.False(store.HoldsACopyOf(["{aaa}", "{bbb}"]));
        Assert.True(store.HoldsACopyOf(["{aaa}"]));
    }

    [Fact]
    public void HoldsACopyOf_JudgesTheNewerRegistryOverAnOlderFile()
    {
        var store = Make();
        store.Save(Both(SavedAt));
        var older = File.ReadAllText(_paths.DnsBackupFile);
        store.Save(Backup("{aaa}", "8.8.8.8") with { SavedAt = SavedAt.AddHours(1) });
        File.WriteAllText(_paths.DnsBackupFile, older);

        Assert.False(store.HoldsACopyOf(["{aaa}", "{bbb}"]));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void Save_RefusesNothingToSave()
    {
        Assert.Throws<ArgumentNullException>(() => Make().Save(null!));
    }

    [Fact]
    public void Save_SaysWhenBothPlacesTookTheCopy()
    {
        Assert.Equal(DnsBackupSaved.BothPlaces, Make().Save(Backup("{aaa}", "8.8.8.8")));
    }

    [Fact]
    public void Save_SaysWhenTheFileWasTheOnlyPlaceThatTookIt()
    {
        // Not a failure and not a success: the machine runs a session on one copy, and only the layer
        // above can carry that to the operator.
        Assert.Equal(
            DnsBackupSaved.FileOnly, Make(new ThrowingMirror()).Save(Backup("{aaa}", "8.8.8.8")));
    }

    [Fact]
    public void Save_SaysTheFileWasTheOnlyPlaceThatTookItWhenTheRegistryHasNoRightsToTheKey()
    {
        // SecurityException is what the registry API throws when the account lacks rights to the key.
        // Attempt's catch filter is shared by both of Save's attempts and must name it too.
        Assert.Equal(
            DnsBackupSaved.FileOnly,
            Make(new ThrowingMirror(() => new SecurityException())).Save(Backup("{aaa}", "8.8.8.8")));
    }

    [Fact]
    public void Save_SaysWhenTheRegistryWasTheOnlyPlaceThatTookIt()
    {
        BlockTheFile();

        Assert.Equal(DnsBackupSaved.RegistryOnly, Make().Save(Backup("{aaa}", "8.8.8.8")));
    }

    [Fact]
    public void Save_PutsANewFileInPlaceOfTheOldOneRatherThanWritingIntoIt()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));

        // Back-dates the file's creation time. A write through the document at the path keeps it; a rename
        // of a finished file over it cannot. That is what stands between a crash mid-save and half of one
        // backup mixed with half of another.
        var whenTheOldFileWasMade = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(_paths.DnsBackupFile, whenTheOldFileWasMade);

        store.Save(Backup("{bbb}", "1.1.1.1"));

        Assert.NotEqual(whenTheOldFileWasMade, File.GetCreationTimeUtc(_paths.DnsBackupFile));
        Assert.Equal(["1.1.1.1"], store.Load()!.Interfaces[0].Servers);
    }

    [Fact]
    public void Save_TakesTheEarlierDocumentOffWhenTheFileHalfFails()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));

        // Something holding the file open stops the write. This one lets the file be deleted; the next test's does not.
        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.Delete);

        Assert.Equal(DnsBackupSaved.RegistryOnly, store.Save(Backup("{bbb}", "1.1.1.1")));

        // Left where it was, the document of the session before would be a second source disagreeing with
        // the registry from the moment this save ended.
        Assert.False(File.Exists(_paths.DnsBackupFile));
    }

    [Fact]
    public void Save_WritesOverTheEarlierDocumentWhenTheFileWillNotGoAtAll()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));

        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(DnsBackupSaved.RegistryOnly, store.Save(Backup("{bbb}", "1.1.1.1")));

        // The file stays but what is in it stops being a copy, so the source stops answering and the
        // previous session's settings cannot be restored.
        Assert.True(File.Exists(_paths.DnsBackupFile));
        Assert.DoesNotContain("8.8.8.8", File.ReadAllText(_paths.DnsBackupFile), StringComparison.Ordinal);
        Assert.Equal("{bbb}", Assert.Single(store.Load()!.Interfaces).Guid);
    }

    [Fact]
    public void Save_ReplacesTheWholeOfTheEarlierCopyAndLeavesNothingBesideIt()
    {
        var store = Make();

        store.Save(Backup("{aaa}", "8.8.8.8"));
        store.Save(Backup("{bbb}", "1.1.1.1"));

        // One file in the directory: a temporary file left behind would be a copy of somebody's settings
        // under a random name, and an appended file is not a document.
        Assert.Equal([_paths.DnsBackupFile], Directory.GetFiles(_paths.DataDirectory));

        var one = Assert.Single(store.Load()!.Interfaces);
        Assert.Equal("{bbb}", one.Guid);
        Assert.Equal(["1.1.1.1"], one.Servers);
        Assert.Equal(File.ReadAllText(_paths.DnsBackupFile), _mirror.Written);
    }

    [Fact]
    public void Save_MakesTheDataDirectoryWhenItIsNotThereYet()
    {
        Directory.Delete(_root, recursive: true);

        Make().Save(Backup("{aaa}", "8.8.8.8"));

        Assert.True(File.Exists(_paths.DnsBackupFile));
    }

    [Fact]
    public void Save_PutsTheCopyInTheRegistryEvenWhenTheFileCannotBeWritten()
    {
        BlockTheFile();

        // No exception: one source is still a machine that can be put back, and refusing to start a
        // session over this would be worse than running on a single copy.
        Make().Save(Backup("{aaa}", "8.8.8.8"));

        Assert.Equal(["8.8.8.8"], Make().Load()!.Interfaces[0].Servers);
    }

    [Fact]
    public void Save_KeepsTheFileCopyWhenTheRegistryRefusesIt()
    {
        var store = Make(new ThrowingMirror());

        store.Save(Backup("{aaa}", "8.8.8.8"));

        Assert.Equal(["8.8.8.8"], store.Load()!.Interfaces[0].Servers);
    }

    [Fact]
    public void Save_ThrowsOnlyWhenNeitherPlaceTookTheCopy()
    {
        BlockTheFile();

        var failure = Assert.Throws<AggregateException>(
            () => Make(new ThrowingMirror()).Save(Backup("{aaa}", "8.8.8.8")));

        // Both were tried: a save that stopped at the first failure would leave a place without a copy that could have had one.
        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Fact]
    public void Clear_TakesBothSourcesOff()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));

        store.Clear();

        Assert.False(File.Exists(_paths.DnsBackupFile));
        Assert.Null(_mirror.Written);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Clear_TakesBothSourcesOffEvenWhenTheFileIsAlreadyGone()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        File.Delete(_paths.DnsBackupFile);

        store.Clear();

        Assert.Null(_mirror.Written);
    }

    [Fact]
    public void Clear_TakesTheFileOffEvenWhenTheRegistryThrows()
    {
        var store = Make(mirror: new ThrowingMirror());
        store.Save(Backup("{aaa}", "8.8.8.8"));

        store.Clear();

        Assert.False(File.Exists(_paths.DnsBackupFile));
    }

    [Fact]
    public void Clear_TakesTheFileOffEvenWhenTheRegistryHasNoRightsToTheKey()
    {
        // The same SecurityException, through Clear's registry-clear attempt: the shared catch filter in
        // Attempt at its other call site.
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));

        var clearer = new DnsBackupStore(_paths, new ThrowingMirror(() => new SecurityException()), _logger);
        clearer.Clear();

        Assert.False(File.Exists(_paths.DnsBackupFile));
    }

    [Fact]
    public void Clear_TakesTheRegistryCopyOffEvenWhenTheFileWillNotGo()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));

        // Held open by something else, as under a backup agent: the file will not go, and the registry copy still has to.
        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.None);

        store.Clear();

        Assert.True(File.Exists(_paths.DnsBackupFile));
        Assert.Equal(1, _mirror.Clears);
        Assert.Null(_mirror.Written);
    }

    [Fact]
    public void Clear_WritesOverTheCopyWhenTheFileItselfWillNotGo()
    {
        var store = Make();
        store.Save(Backup("{aaa}", "8.8.8.8"));

        // A hold that stops the file being removed and still lets it be written into.
        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        store.Clear();

        // The session is over, so the settings in that document are nobody's to restore. A file left
        // holding them would answer a Load on the next run.
        Assert.True(File.Exists(_paths.DnsBackupFile));
        Assert.DoesNotContain("8.8.8.8", File.ReadAllText(_paths.DnsBackupFile), StringComparison.Ordinal);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Clear_OnAMachineWithNoCopyAnywhereIsNotAnError()
    {
        Make().Clear();

        Assert.Equal(1, _mirror.Clears);
    }

    [Fact]
    public void Clear_OnAMachineWhereNothingWasEverWrittenReportsNothing()
    {
        // The data directory is not there at all, as when a clean or a recover runs before anything was
        // saved. Nothing to remove is not a failure and must not log one.
        Directory.Delete(_root, recursive: true);

        Make().Clear();

        Assert.Equal(1, _mirror.Clears);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void ABackupOfNoInterfacesIsNotOne()
    {
        Assert.False(new DnsBackup(SavedAt, []).IsUsable());
        Assert.True(new DnsBackup(SavedAt, [new InterfaceDnsState("{aaa}", 7, "Wi-Fi", IsDhcp: true, [])]).IsUsable());
    }

    [Fact]
    public void ABackupWithNoListAtAllIsNotOneEither()
    {
        // What a document that parsed without an interfaces field deserializes into.
        Assert.False(new DnsBackup(SavedAt, null!).IsUsable());
    }

    [Fact]
    public void ABackupWithAHoleWhereAnInterfaceShouldBeIsNotOne()
    {
        // What {"interfaces":[null]} deserializes into. Counted, it passes for a copy; restored from, it
        // throws partway, leaving some interfaces on 127.0.0.1.
        Assert.False(new DnsBackup(SavedAt, [null!]).IsUsable());
        Assert.False(new DnsBackup(
            SavedAt,
            [new InterfaceDnsState("{aaa}", 7, "Wi-Fi", IsDhcp: true, ["8.8.8.8"]), null!]).IsUsable());
    }

    [Fact]
    public void ABackupWhoseInterfaceCarriesNoListOfServersIsNotOne()
    {
        // The same hole one level down: an interface with no servers field. An empty list is a real answer (no servers); null is not.
        Assert.False(new DnsBackup(
            SavedAt, [new InterfaceDnsState("{aaa}", 7, "Wi-Fi", IsDhcp: false, null!)]).IsUsable());
    }

    [Fact]
    public void Load_AnswersFromTheRegistryWhenTheFileNamesAHoleInsteadOfAnInterface()
    {
        // The document form of the case above, through the reader a restore uses.
        _mirror.Written = RegistryCopy;
        File.WriteAllText(
            _paths.DnsBackupFile, """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[null]}""");

        Assert.Equal("{bbb}", Assert.Single(Make().Load()!.Interfaces).Guid);
    }

    [Fact]
    public void ThePlaceTheMachineKeepsItsCopy()
    {
        // Written out rather than taken from the class: a mistyped key or value name writes the copy where nothing reads it.
        Assert.Equal(@"SOFTWARE\Chronos", RegistryBackupMirror.Key);
        Assert.Equal("DnsBackup", RegistryBackupMirror.ValueName);
    }

    [Fact]
    public void TheMirrorWritesTheCopyUnderThatNameAndReadsItBack()
    {
        using var scratch = new ScratchKey();

        scratch.Mirror.Write(RegistryCopy);

        // Read through the registry rather than the mirror: the name is the point.
        Assert.Equal(RegistryCopy, scratch.Get("DnsBackup"));
        Assert.Equal(RegistryCopy, scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorMakesTheKeyWhenTheMachineHasNoneYet()
    {
        // First session on a fresh installation: nothing has ever written under the product's key.
        using var scratch = new ScratchKey();

        Assert.False(scratch.KeyExists);

        scratch.Mirror.Write(RegistryCopy);

        Assert.Equal(RegistryCopy, scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorWritesOverTheCopyOfAnEarlierSession()
    {
        using var scratch = new ScratchKey();
        scratch.Mirror.Write(RegistryCopy);

        scratch.Mirror.Write("""{"savedAt":"2026-09-04T00:00:00+00:00","interfaces":[]}""");

        Assert.Equal("""{"savedAt":"2026-09-04T00:00:00+00:00","interfaces":[]}""", scratch.Mirror.Read());
    }

    [Fact]
    public void TheCopyIsKeptAsAPlainStringAndComesBackAsItWasWritten()
    {
        // An expandable string would have Windows rewrite text between percent signs on the way out, and an
        // adapter named after one would be restored as a different machine.
        using var scratch = new ScratchKey();
        const string Named =
            """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[{"guid":"{aaa}","index":7,"name":"%TEMP% Adapter","isDhcp":false,"servers":["8.8.8.8"]}]}""";

        scratch.Mirror.Write(Named);

        Assert.Equal(RegistryValueKind.String, scratch.KindOf("DnsBackup"));
        Assert.Equal(Named, scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorReadsTheCopyWithoutAskingForTheRightToWriteIt()
    {
        // Under HKLM an unelevated account may read but not write. A read that opens the key for writing is
        // refused, so chronos diag would report no backup and an ordinary-account restore would find nothing.
        using var scratch = new ScratchKey();
        scratch.Mirror.Write(RegistryCopy);
        scratch.AllowReadingOnly();

        Assert.Equal(RegistryCopy, scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorHasNothingToSayAboutAMachineWithNoKey()
    {
        using var scratch = new ScratchKey();

        Assert.Null(scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorReadsNothingButItsOwnValue()
    {
        // The product's key belongs to the product as a whole; everything else under it is not a backup.
        using var scratch = new ScratchKey();
        scratch.Set("InstallPath", @"C:\Program Files\Chronos", RegistryValueKind.String);
        scratch.Set("DnsBackupOld", RegistryCopy, RegistryValueKind.String);

        Assert.Null(scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorReadsAValueThatIsNotAStringAsNoCopyAtAll()
    {
        using var scratch = new ScratchKey();
        scratch.Set("DnsBackup", 1, RegistryValueKind.DWord);

        Assert.Null(scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorRefusesToWriteADocumentThatIsNotOne()
    {
        // A blank value is a source claiming a copy that restores nothing.
        using var scratch = new ScratchKey();

        Assert.Throws<ArgumentException>(() => scratch.Mirror.Write("   "));
        Assert.Throws<ArgumentNullException>(() => scratch.Mirror.Write(null!));
        Assert.False(scratch.KeyExists);
    }

    [Fact]
    public void ClearingTheMirrorTakesTheCopyAndLeavesTheRestOfTheKey()
    {
        using var scratch = new ScratchKey();
        scratch.Mirror.Write(RegistryCopy);
        scratch.Set("InstallPath", @"C:\Program Files\Chronos", RegistryValueKind.String);

        scratch.Mirror.Clear();

        Assert.Null(scratch.Mirror.Read());

        // The key belongs to the product, not this layer: removing it would take the installation's own values too.
        Assert.Equal(@"C:\Program Files\Chronos", scratch.Get("InstallPath"));
    }

    [Fact]
    public void ClearingTheMirrorOfAMachineThatHasNoCopyIsNotAnError()
    {
        using var scratch = new ScratchKey();

        // No key at all, and then a key with everything but this value in it.
        scratch.Mirror.Clear();
        scratch.Set("InstallPath", @"C:\Program Files\Chronos", RegistryValueKind.String);
        scratch.Mirror.Clear();

        Assert.Equal(@"C:\Program Files\Chronos", scratch.Get("InstallPath"));
    }

    [Fact]
    public void TheStoreKeepsARealRegistryCopyItCanRestoreFrom()
    {
        // The document the store writes must survive being a registry value, and be found there when the file is gone.
        using var scratch = new ScratchKey();
        var store = new DnsBackupStore(_paths, scratch.Mirror, _logger);

        store.Save(new DnsBackup(
            SavedAt, [new InterfaceDnsState("{aaa}", 22, "Wi-Fi", IsDhcp: true, ["1.1.1.1", "9.9.9.9"])]));
        File.Delete(_paths.DnsBackupFile);

        var one = Assert.Single(store.Load()!.Interfaces);
        Assert.Equal("{aaa}", one.Guid);
        Assert.Equal(22, one.Index);
        Assert.Equal("Wi-Fi", one.Name);
        Assert.True(one.IsDhcp);
        Assert.Equal(["1.1.1.1", "9.9.9.9"], one.Servers);

        store.Clear();
        Assert.Null(scratch.Mirror.Read());
    }

    [Fact]
    public void TheMirrorOfTheMachineIsKeptInTheMachinesHive()
    {
        // The service runs as LocalSystem: a copy in the hive of whoever started the session would never be
        // found. Read off the mirror, without opening either real hive.
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Chronos", new RegistryBackupMirror().Location);

        // And the location is the mirror's own root and key, not a fixed string.
        using var scratch = new ScratchKey();
        Assert.Equal($@"HKEY_CURRENT_USER\{scratch.Branch}", scratch.Mirror.Location);
    }

    [Fact]
    public void TheProductKeyTheUninstallRemovesIsTheMachinesOwn()
    {
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Chronos", ProductKey.MachineLocation);
    }

    [Fact]
    public void TheProductKeyGoesWhenItHoldsNothing()
    {
        using var scratch = new ScratchKey();
        scratch.Mirror.Write(RegistryCopy);
        scratch.Mirror.Clear();

        Assert.Equal(ProductKeyRemoval.Removed, scratch.RemoveIfEmpty());
        Assert.False(scratch.KeyExists);
    }

    [Fact]
    public void TheProductKeyStaysWhileItHoldsAValue()
    {
        // A kept DnsBackup is the copy of a restore that did not finish.
        using var scratch = new ScratchKey();
        scratch.Mirror.Write(RegistryCopy);

        Assert.Equal(ProductKeyRemoval.KeptNotEmpty, scratch.RemoveIfEmpty());
        Assert.Equal(RegistryCopy, scratch.Mirror.Read());
    }

    [Fact]
    public void TheProductKeyStaysWhileItHoldsAKeyOfItsOwn()
    {
        using var scratch = new ScratchKey();
        scratch.AddSubKey("Other");

        Assert.Equal(ProductKeyRemoval.KeptNotEmpty, scratch.RemoveIfEmpty());
        Assert.True(scratch.KeyExists);
    }

    [Fact]
    public void AMachineWithNoProductKeyHasNothingToRemove()
    {
        using var scratch = new ScratchKey();

        Assert.Equal(ProductKeyRemoval.NotThere, scratch.RemoveIfEmpty());
    }

    [Fact]
    public void ThereIsNoMirrorWithoutARootOrAKeyToKeepItUnder()
    {
        Assert.Throws<ArgumentNullException>(() => new RegistryBackupMirror(null!, RegistryBackupMirror.Key));
        Assert.Throws<ArgumentException>(() => new RegistryBackupMirror(Registry.CurrentUser, "   "));
    }

    [Fact]
    public void AFileThatStaysUnreadableIsWarnedAboutOnceAcrossLoads()
    {
        // The service loads every 15 s while it holds nothing; one Warning per condition.
        Make().Save(Backup("{aaa}", "8.8.8.8"));
        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var store = Make();

        store.Load();
        store.Load();
        store.Load();

        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("file could not be read"));
    }

    [Fact]
    public void ARegistryThatStaysUnreadableIsWarnedAboutOnceAcrossLoads()
    {
        var store = Make(new ThrowingMirror());

        store.Load();
        store.Load();

        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("registry could not be read"));
    }

    [Fact]
    public void ACopyThatStaysEmptyIsWarnedAboutOnceAcrossLoads()
    {
        File.WriteAllText(_paths.DnsBackupFile, """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[]}""");
        var store = Make();

        store.Load();
        store.Load();

        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void AnotherConditionOfTheSameSourceIsWarnedAboutAgain()
    {
        var store = Make();
        File.WriteAllText(_paths.DnsBackupFile, """{"savedAt":"2026-09-03T10:30:00+03:00","interfaces":[]}""");
        store.Load();
        File.WriteAllText(_paths.DnsBackupFile, "not a document");

        store.Load();

        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public void AConditionThatCameBackAfterAGoodReadIsWarnedAboutAgain()
    {
        var store = Make();
        File.WriteAllText(_paths.DnsBackupFile, "not a document");
        store.Load();
        store.Save(Backup("{aaa}", "8.8.8.8"));
        store.Load();
        File.WriteAllText(_paths.DnsBackupFile, "not a document");

        store.Load();

        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public void AnUnreadableFileDoesNotQuietTheRegistry()
    {
        // One memory per place: the file's condition says nothing about the registry's.
        File.WriteAllText(_paths.DnsBackupFile, "not a document");
        _mirror.Written = "not a document";
        var store = Make();

        store.Load();

        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public void AskingWhetherACopyIsHeldNeitherWarnsNorQuietsTheNextLoad()
    {
        File.WriteAllText(_paths.DnsBackupFile, "not a document");
        var store = Make();

        store.HoldsACopy();
        store.Load();

        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void AskingWhetherACopyIsHeldOfAFileThatCannotBeReadDoesNotQuietTheNextLoad()
    {
        Make().Save(Backup("{aaa}", "8.8.8.8"));
        using var held = new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var store = Make();

        store.HoldsACopy();
        store.Load();

        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("file could not be read"));
    }

    [Fact]
    public void ARegistryReadFineAgainIsWarnedAboutAgainWhenItFailsOnceMore()
    {
        var mirror = new MemoryMirror { Refuses = true };
        var store = Make(mirror);
        store.Load();
        mirror.Refuses = false;
        store.Load();
        mirror.Refuses = true;

        store.Load();

        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("registry could not be read")));
    }

    [Fact]
    public void AConditionThatStaysIsRepeatedAtDebugAndNeverAbove()
    {
        // The service loads every 15 s while it holds nothing.
        var store = Make(new ThrowingMirror());
        store.Load();
        var said = _logger.Entries.Count;

        store.Load();
        store.Load();

        Assert.Equal(2, _logger.Entries.Count - said);
        Assert.All(_logger.Entries.Skip(said), entry => Assert.Equal(LogLevel.Debug, entry.Level));
    }

    /// <summary>The registry half, as a thing this file can look inside.</summary>
    private sealed class RecordingMirror : IBackupMirror
    {
        public string? Written { get; set; }

        public int Reads { get; set; }

        public int Clears { get; private set; }

        public void Write(string json) => Written = json;

        public string? Read()
        {
            Reads++;

            return Written;
        }

        public void Clear()
        {
            Clears++;
            Written = null;
        }
    }

    /// <summary>
    /// A registry this account may not touch. Throws UnauthorizedAccessException by default; a test
    /// that needs the SecurityException a locked-down HKLM key throws passes its own failure.
    /// </summary>
    private sealed class ThrowingMirror(Func<Exception>? failure = null) : IBackupMirror
    {
        private readonly Func<Exception> _failure = failure ?? (() => new UnauthorizedAccessException());

        public void Write(string json) => throw _failure();

        public string? Read() => throw _failure();

        public void Clear() => throw _failure();
    }

    /// <summary>A key of this test's own, removed afterwards pass or fail. Under HKCU because a suite does not write to HKLM.</summary>
    private sealed class ScratchKey : IDisposable
    {
        private const string Root = @"Software\Chronos.Service.Tests";

        private readonly string _branch = $@"{Root}\{Guid.NewGuid():N}";

        private bool _lockedDown;

        /// <summary>The mirror itself, pointed at this key.</summary>
        public RegistryBackupMirror Mirror => new(Registry.CurrentUser, _branch);

        public string Branch => _branch;

        /// <summary>The uninstall's removal of the product key, pointed at this key.</summary>
        public ProductKeyRemoval RemoveIfEmpty() => ProductKey.RemoveIfEmpty(Registry.CurrentUser, _branch);

        public void AddSubKey(string name) => Registry.CurrentUser.CreateSubKey($@"{_branch}\{name}").Dispose();

        /// <summary>Whether the key is there at all.</summary>
        public bool KeyExists
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(_branch, writable: false);

                return key is not null;
            }
        }

        public void Set(string name, object value, RegistryValueKind kind)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_branch);

            key.SetValue(name, value, kind);
        }

        public object? Get(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(_branch, writable: false);

            return key?.GetValue(name);
        }

        public RegistryValueKind KindOf(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(_branch, writable: false);

            return key!.GetValueKind(name);
        }

        /// <summary>
        /// Leaves this account able to read the key and nothing else, as an unelevated account is under
        /// HKLM. The rights are put back before the key goes, or nothing could remove it.
        /// </summary>
        public void AllowReadingOnly()
        {
            Rights(RegistryRights.ReadKey, inherit: false);
            _lockedDown = true;
        }

        private void Rights(RegistryRights rights, bool inherit)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                _branch,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!;

            var access = key.GetAccessControl(AccessControlSections.Access);
            access.SetAccessRuleProtection(isProtected: !inherit, preserveInheritance: false);
            access.AddAccessRule(
                new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Allow));

            key.SetAccessControl(access);
        }

        public void Dispose()
        {
            if (_lockedDown)
            {
                Rights(RegistryRights.FullControl, inherit: true);
            }

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
