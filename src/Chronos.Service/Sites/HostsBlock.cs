using System.Text;

namespace Chronos.Service.Sites;

/// <summary>
/// Text surgery on the hosts file. A block runs from its begin marker line through the end marker
/// line's terminator; the line break before it is never touched, so foreign lines are not merged.
/// A last line without a terminator gains one when a block is appended. Every marker pair is
/// removed and a write collapses extra pairs into one, so a restored or hand-pasted block cannot
/// outlive <c>chronos clean</c>. A begin marker without an end marker comes from a hand edit (writes
/// are atomic), so the block ends at the first line that is neither a comment nor ours.
/// </summary>
internal static class HostsBlock
{
    public const string BeginMarker = "# ===== CHRONOS BEGIN";
    public const string EndMarker = "# ===== CHRONOS END";

    private const string NewLine = "\r\n";

    public static IReadOnlyList<string>? Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var blocks = Locate(content);
        if (blocks.Count == 0)
        {
            return null;
        }

        // Every block contributes, so a duplicate reads as a difference and gets collapsed.
        var lines = new List<string>();
        foreach (var (start, end) in blocks)
        {
            foreach (var line in EnumerateLines(content[start..end]))
            {
                if (line.Length == 0
                    || line.StartsWith(BeginMarker, StringComparison.Ordinal)
                    || line.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add(line);
            }
        }

        return lines;
    }

    public static string Write(string content, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(lines);

        var builder = new StringBuilder();
        builder.Append(BeginMarker).Append(NewLine);
        foreach (var line in lines)
        {
            builder.Append(line).Append(NewLine);
        }

        builder.Append(EndMarker).Append(NewLine);
        var block = builder.ToString();

        var blocks = Locate(content);
        if (blocks.Count == 0)
        {
            return content.Length == 0
                ? block
                : content + (EndsWithLineBreak(content) ? string.Empty : NewLine) + block;
        }

        // The first block is rewritten in place; the rest are dropped, so one block remains.
        var result = new StringBuilder(content[..blocks[0].Start]).Append(block);
        for (var i = 1; i < blocks.Count; i++)
        {
            result.Append(content[blocks[i - 1].End..blocks[i].Start]);
        }

        return result.Append(content[blocks[^1].End..]).ToString();
    }

    public static string Remove(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var blocks = Locate(content);
        if (blocks.Count == 0)
        {
            return content;
        }

        var result = new StringBuilder(content[..blocks[0].Start]);
        for (var i = 1; i < blocks.Count; i++)
        {
            result.Append(content[blocks[i - 1].End..blocks[i].Start]);
        }

        return result.Append(content[blocks[^1].End..]).ToString();
    }

    private static bool EndsWithLineBreak(string content) => content[^1] is '\n' or '\r';

    private static List<(int Start, int End)> Locate(string content)
    {
        var blocks = new List<(int Start, int End)>();
        var index = 0;

        while (index < content.Length)
        {
            var (line, next) = ReadLine(content, index);
            if (line.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                var end = FindEnd(content, next);
                blocks.Add((index, end));
                index = end;
                continue;
            }

            index = next;
        }

        return blocks;
    }

    /// <param name="index">First character of the line after the begin marker.</param>
    private static int FindEnd(string content, int index)
    {
        var scan = index;
        while (scan < content.Length)
        {
            var (line, next) = ReadLine(content, scan);
            if (line.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                return next;
            }

            scan = next;
        }

        // No end marker: claim only lines that look like ours; the rest is someone else's.
        while (index < content.Length)
        {
            var (line, next) = ReadLine(content, index);
            if (!IsOurs(line))
            {
                return index;
            }

            index = next;
        }

        return content.Length;
    }

    private static bool IsOurs(string line)
    {
        if (line.StartsWith('#'))
        {
            return true;
        }

        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 0 && fields[0] is HostEntries.BlockedIPv4 or HostEntries.BlockedIPv6;
    }

    private static (string Line, int Next) ReadLine(string content, int index)
    {
        var breakAt = content.IndexOf('\n', index);
        var lineEnd = breakAt < 0 ? content.Length : breakAt;

        return (content[index..lineEnd].TrimEnd('\r'), breakAt < 0 ? content.Length : breakAt + 1);
    }

    private static IEnumerable<string> EnumerateLines(string region)
    {
        var index = 0;
        while (index < region.Length)
        {
            var (line, next) = ReadLine(region, index);
            yield return line;
            index = next;
        }
    }
}
