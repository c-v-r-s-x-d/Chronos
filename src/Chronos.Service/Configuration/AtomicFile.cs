namespace Chronos.Service.Configuration;

internal static class AtomicFile
{
    /// <summary>Writes via a temporary file so a crash mid-write leaves the previous version intact.</summary>
    public static void Write(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"Path '{path}' has no directory.", nameof(path));

        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // A failed rename would otherwise leave the temp file behind for good.
            Discard(temporary);
        }
    }

    private static void Discard(string temporary)
    {
        try
        {
            // Already gone after a successful rename.
            File.Delete(temporary);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Throwing here would replace the write's own failure.
        }
    }
}
