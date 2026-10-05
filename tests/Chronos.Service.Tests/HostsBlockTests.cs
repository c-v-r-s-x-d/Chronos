using Chronos.Service.Sites;

namespace Chronos.Service.Tests;

public sealed class HostsBlockTests
{
    private static readonly string[] Lines = ["0.0.0.0 example.com", ":: example.com"];

    [Fact]
    public void Read_ReturnsNullWhenThereIsNoBlock()
    {
        Assert.Null(HostsBlock.Read("127.0.0.1 localhost\r\n"));
    }

    [Fact]
    public void Write_PutsTheLinesBetweenMarkers()
    {
        var result = HostsBlock.Write(string.Empty, Lines);

        Assert.Contains(HostsBlock.BeginMarker, result, StringComparison.Ordinal);
        Assert.Contains(HostsBlock.EndMarker, result, StringComparison.Ordinal);
        Assert.Equal(Lines, HostsBlock.Read(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1 localhost\r\n")]
    [InlineData("# a comment\r\n\r\n10.0.0.1 intranet\t# trailing\r\n")]
    public void WriteThenRemove_RestoresTheOriginalByteForByte(string original)
    {
        // The file belongs to whoever else edits it; our round trip must be invisible.
        var withBlock = HostsBlock.Write(original, Lines);

        Assert.Equal(original, HostsBlock.Remove(withBlock));
    }

    [Fact]
    public void WriteThenRemove_AddsTheTerminatorAFileWithoutOneWasMissing()
    {
        // The documented cost of never consuming a line break that is not ours: a file whose last
        // line has no terminator gains one. The change is additive; no lines are merged.
        var withBlock = HostsBlock.Write("127.0.0.1 localhost", Lines);

        Assert.Equal("127.0.0.1 localhost\r\n", HostsBlock.Remove(withBlock));
    }

    [Fact]
    public void Remove_KeepsTheLineBreakBetweenForeignLines()
    {
        // A block we did not write sits between two foreign lines; consuming the line break in
        // front of it would merge them into one.
        var content = "before\r\n"
            + HostsBlock.BeginMarker + "\r\n0.0.0.0 example.com\r\n" + HostsBlock.EndMarker + "\r\n"
            + "after\r\n";

        Assert.Equal("before\r\nafter\r\n", HostsBlock.Remove(content));
    }

    [Fact]
    public void Remove_StripsEveryMarkerPair()
    {
        // A restored backup over an active session leaves two blocks; one that survives clean is
        // a live 0.0.0.0 entry with no way out but hand-editing a system file.
        var content = "before\r\n" + RawBlock("0.0.0.0 a.com") + "between\r\n" + RawBlock("0.0.0.0 b.com") + "after\r\n";

        Assert.Equal("before\r\nbetween\r\nafter\r\n", HostsBlock.Remove(content));
    }

    [Fact]
    public void Write_CollapsesASecondMarkerPairIntoOne()
    {
        var content = "before\r\n" + RawBlock("0.0.0.0 a.com") + "between\r\n" + RawBlock("0.0.0.0 b.com") + "after\r\n";

        var rewritten = HostsBlock.Write(content, ["0.0.0.0 other.com"]);

        Assert.Equal(["0.0.0.0 other.com"], HostsBlock.Read(rewritten));
        Assert.Equal("before\r\nbetween\r\nafter\r\n", HostsBlock.Remove(rewritten));
    }

    [Fact]
    public void Read_ReportsTheLinesOfEveryBlock()
    {
        // The enforcer compares what it reads against the plan, so a duplicate block has to show
        // up as a difference or nothing would ever collapse it.
        var content = RawBlock("0.0.0.0 a.com") + RawBlock("0.0.0.0 b.com");

        Assert.Equal(["0.0.0.0 a.com", "0.0.0.0 b.com"], HostsBlock.Read(content));
    }

    [Fact]
    public void Remove_KeepsWhatFollowsADamagedBlock()
    {
        // A begin marker with no end marker comes from a hand edit, which is exactly the case
        // where foreign lines follow it. The block ends at the first line that is not ours.
        var content = "before\r\n" + HostsBlock.BeginMarker + "\r\n0.0.0.0 stale.com\r\n"
            + "10.0.0.1 intranet\r\n127.0.0.1 localhost\r\n";

        Assert.Equal("before\r\n10.0.0.1 intranet\r\n127.0.0.1 localhost\r\n", HostsBlock.Remove(content));
    }

    [Fact]
    public void Write_KeepsWhatFollowsADamagedBlock()
    {
        var content = "before\r\n" + HostsBlock.BeginMarker + "\r\n0.0.0.0 stale.com\r\n10.0.0.1 intranet\r\n";

        var rewritten = HostsBlock.Write(content, Lines);

        Assert.Equal(Lines, HostsBlock.Read(rewritten));
        Assert.Equal("before\r\n10.0.0.1 intranet\r\n", HostsBlock.Remove(rewritten));
    }

    private static string RawBlock(params string[] lines) =>
        HostsBlock.BeginMarker + "\r\n"
        + string.Concat(lines.Select(line => line + "\r\n"))
        + HostsBlock.EndMarker + "\r\n";

    [Fact]
    public void Write_IsStableWhenRepeated()
    {
        var once = HostsBlock.Write("127.0.0.1 localhost\r\n", Lines);

        Assert.Equal(once, HostsBlock.Write(once, Lines));
    }

    [Fact]
    public void Write_KeepsForeignLinesThatFollowTheBlock()
    {
        var withBlock = HostsBlock.Write("before\r\n", Lines) + "after\r\n";

        var rewritten = HostsBlock.Write(withBlock, ["0.0.0.0 other.com"]);

        Assert.StartsWith("before\r\n", rewritten, StringComparison.Ordinal);
        Assert.EndsWith("after\r\n", rewritten, StringComparison.Ordinal);
        Assert.Equal(["0.0.0.0 other.com"], HostsBlock.Read(rewritten));
    }

    [Fact]
    public void Remove_LeavesContentWithoutABlockAlone()
    {
        const string original = "127.0.0.1 localhost\r\n";

        Assert.Equal(original, HostsBlock.Remove(original));
    }

    [Fact]
    public void ADamagedBlockIsRepairedRatherThanDuplicated()
    {
        // A begin marker with no end marker is what a hand edit leaves behind; the write is
        // atomic, so a crash cannot produce it.
        var damaged = "before\r\n" + HostsBlock.BeginMarker + "\r\n0.0.0.0 stale.com\r\n";

        var repaired = HostsBlock.Write(damaged, Lines);

        Assert.Equal(Lines, HostsBlock.Read(repaired));
        Assert.Equal("before\r\n", HostsBlock.Remove(repaired));
    }

    [Fact]
    public void Read_IgnoresTheMarkersAndBlankLines()
    {
        var content = HostsBlock.BeginMarker + "\r\n\r\n0.0.0.0 example.com\r\n" + HostsBlock.EndMarker + "\r\n";

        Assert.Equal(["0.0.0.0 example.com"], HostsBlock.Read(content));
    }

    [Fact]
    public void Read_ToleratesLineFeedOnlyEndings()
    {
        var content = HostsBlock.BeginMarker + "\n0.0.0.0 example.com\n" + HostsBlock.EndMarker + "\n";

        Assert.Equal(["0.0.0.0 example.com"], HostsBlock.Read(content));
    }
}
