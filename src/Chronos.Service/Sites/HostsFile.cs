using System.Text;

namespace Chronos.Service.Sites;

public sealed class HostsFile
{
    // Latin1 round-trips every byte, so a hosts file in any encoding survives a rewrite. UTF-8
    // would turn a cp1251 comment into U+FFFD and write that loss back. No BOM detection either.
    private static readonly Encoding HostsEncoding = Encoding.Latin1;

    public HostsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FilePath = path;
    }

    public static string SystemPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public string FilePath { get; }

    /// <summary>The file a write goes through. Fixed name, so a copy left by a killed process is recognisable.</summary>
    public string TemporaryPath => Path.Combine(DirectoryPath, "hosts.chronos-tmp");

    private string DirectoryPath => Path.GetDirectoryName(FilePath)
        ?? throw new InvalidOperationException($"Path '{FilePath}' has no directory.");

    /// <summary>The lines of the Chronos block, or null when there is none. Here so the diagnostic package shares the parsing.</summary>
    public IReadOnlyList<string>? ReadBlock() => HostsBlock.Read(Read());

    public string Read()
    {
        if (!File.Exists(FilePath))
        {
            return string.Empty;
        }

        using var reader = new StreamReader(FilePath, HostsEncoding, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes through a temporary file in the same directory and replaces the original, so the
    /// permissions and attributes of the system file survive the write.
    /// </summary>
    public void Write(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var temporary = TemporaryPath;

        try
        {
            WriteThrough(temporary, content);

            if (File.Exists(FilePath))
            {
                // File.Replace keeps the destination's ACL and attributes; File.Move would use the
                // temp file's. ignoreMetadataErrors stays false: ReplaceFile silently skips the ACL
                // merge without WRITE_DAC, so errors must be raised, not tolerated.
                File.Replace(temporary, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temporary, FilePath);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void WriteThrough(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(HostsEncoding.GetBytes(content));

        // NTFS journals metadata, not data; without this a power loss can leave a truncated hosts file.
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Must not replace the original error.
        }
    }
}
