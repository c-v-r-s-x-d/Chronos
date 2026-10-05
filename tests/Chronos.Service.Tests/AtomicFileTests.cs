using Chronos.Service.Configuration;

namespace Chronos.Service.Tests;

/// <summary>The write config.json, state.json and the DNS backup go through: the file at the path is always a whole document.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private const string Previous = """{"version":1,"blocked":["example.com"]}""";

    private const string Next = """{"version":2,"blocked":["example.com","example.org"]}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));

    private readonly string _path;

    public AtomicFileTests()
    {
        _path = Path.Combine(_root, "document.json");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Write_PutsTheDocumentAtThePathAndNothingBesideIt()
    {
        AtomicFile.Write(_path, Next);

        Assert.Equal(Next, File.ReadAllText(_path));
        Assert.Equal([_path], Directory.GetFiles(_root));
    }

    [Fact]
    public void Write_MakesTheDirectoryWhenItIsNotThereYet()
    {
        var deeper = Path.Combine(_root, "not", "there", "yet", "document.json");

        AtomicFile.Write(deeper, Next);

        Assert.Equal(Next, File.ReadAllText(deeper));
    }

    [Fact]
    public void Write_PutsANewFileInPlaceOfTheOldOneRatherThanWritingIntoIt()
    {
        File.WriteAllText(_path, Previous);

        // Back-dates the creation time. A write through the file at the path keeps it; a rename of a
        // finished file over it cannot, since a different file ends up there. That is what "atomic" buys:
        // no instant at which the path holds half of each version.
        var whenTheOldFileWasMade = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(_path, whenTheOldFileWasMade);

        AtomicFile.Write(_path, Next);

        Assert.NotEqual(whenTheOldFileWasMade, File.GetCreationTimeUtc(_path));
        Assert.Equal(Next, File.ReadAllText(_path));
    }

    [Fact]
    public void Write_LeavesTheEarlierDocumentWholeWhenItCannotBeReplaced()
    {
        File.WriteAllText(_path, Previous);

        // A reader that lets writers in (antivirus, a backup agent). The rename cannot go through while
        // anything holds the file, and the write must fail rather than change the file under the reader.
        using var scanner = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.Write(_path, Next));
        Assert.Equal(Previous, File.ReadAllText(_path));
    }

    [Fact]
    public void Write_LeavesNoCopyOfTheDocumentBehindWhenTheRenameFails()
    {
        // Something that is not a file already stands at the path. The document was already written in
        // full under a random name in the same directory; it must be removed, since nothing later looks
        // for it (for the DNS backup it is every resolver on the machine).
        var blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(blocked);

        Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.Write(blocked, Next));

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Write_RefusesAPathWithNoDirectoryToWriteIn()
    {
        Assert.Throws<ArgumentException>(() => AtomicFile.Write(Path.GetPathRoot(_root)!, Next));
    }
}
