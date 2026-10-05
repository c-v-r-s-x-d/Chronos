using Chronos.Core.Rules;

namespace Chronos.Service.Apps;

/// <summary>
/// The armed rule set, shaped for the per-process-start question: is this image blocked?
/// </summary>
public sealed class AppRuleIndex
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private readonly HashSet<string> _fullPaths;
    private readonly HashSet<string> _fileNames;

    private AppRuleIndex(HashSet<string> fullPaths, HashSet<string> fileNames)
    {
        _fullPaths = fullPaths;
        _fileNames = fileNames;
    }

    public static AppRuleIndex Empty { get; } = new(new(Comparer), new(Comparer));

    public bool IsEmpty => _fullPaths.Count is 0 && _fileNames.Count is 0;

    public int Count => _fullPaths.Count + _fileNames.Count;

    /// <summary>
    /// Whether any full-path rule is armed. Start events carry only a bare file name, so resolving
    /// a full path costs a system call; skip it when this is false.
    /// </summary>
    public bool HasFullPathRules => _fullPaths.Count > 0;

    public static AppRuleIndex Build(IEnumerable<AppRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var fullPaths = new HashSet<string>(Comparer);
        var fileNames = new HashSet<string>(Comparer);

        foreach (var rule in rules)
        {
            switch (rule.Kind)
            {
                case AppMatchKind.FullPath:
                    fullPaths.Add(ImagePath.Normalize(rule.Value));
                    break;

                case AppMatchKind.FileName:
                    // A path typed into the file-name field still means the file name.
                    fileNames.Add(ImagePath.FileNameOf(rule.Value));
                    break;

                default:
                    // Unknown kind: skipped, not widened to a file-name match.
                    break;
            }
        }

        return new AppRuleIndex(fullPaths, fileNames);
    }

    /// <summary>Cheap check: a bare file name, or the tail of a path, against the file-name rules.</summary>
    public bool MatchesFileName(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);

        return !string.IsNullOrWhiteSpace(imagePath) && _fileNames.Contains(ImagePath.FileNameOf(imagePath));
    }

    /// <summary>Expensive check (normalizing makes system calls); consult <see cref="HasFullPathRules"/> first.</summary>
    public bool MatchesFullPath(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        return _fullPaths.Count > 0
            && !string.IsNullOrWhiteSpace(fullPath)
            && _fullPaths.Contains(ImagePath.Normalize(fullPath));
    }

    public bool SameAs(AppRuleIndex other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return _fullPaths.SetEquals(other._fullPaths) && _fileNames.SetEquals(other._fileNames);
    }
}
