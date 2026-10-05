using System.Text;
using Chronos.Core.Rules;

namespace Chronos.Service.Sites;

internal static class HostEntries
{
    public const string BlockedIPv4 = "0.0.0.0";
    public const string BlockedIPv6 = "::";

    // Tells anyone who opens the hosts file how to remove the block.
    public const string Header =
        "# Managed by Chronos. Lines inside this block are rewritten while a session is active. "
        + "Run 'chronos clean' to remove the block.";

    private const string WwwPrefix = "www.";

    /// <summary>
    /// Host names to block, ordered so an unchanged plan never rewrites the file. Non-ASCII
    /// domains are skipped: the file is Latin1 and cannot hold them.
    /// </summary>
    public static IReadOnlyList<string> Names(IEnumerable<SiteRule> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            if (!Ascii.IsValid(site.Domain))
            {
                continue;
            }

            names.Add(site.Domain);
            if (!site.Domain.StartsWith(WwwPrefix, StringComparison.Ordinal))
            {
                names.Add(WwwPrefix + site.Domain);
            }
        }

        return [.. names];
    }

    public static IReadOnlyList<string> Render(IEnumerable<SiteRule> sites)
    {
        var lines = new List<string> { Header };

        foreach (var name in Names(sites))
        {
            lines.Add($"{BlockedIPv4} {name}");
            lines.Add($"{BlockedIPv6} {name}");
        }

        return lines;
    }

    /// <summary>How many domains <see cref="Names"/> drops because they are not pure ASCII.</summary>
    public static int SkippedCount(IEnumerable<SiteRule> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        return sites.Count(site => !Ascii.IsValid(site.Domain));
    }

    /// <summary>Host names a block currently carries.</summary>
    public static IReadOnlyList<string> NamesIn(IEnumerable<string> blockLines)
    {
        ArgumentNullException.ThrowIfNull(blockLines);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in blockLines)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[0] is BlockedIPv4 or BlockedIPv6)
            {
                names.Add(fields[1]);
            }
        }

        return [.. names];
    }
}
