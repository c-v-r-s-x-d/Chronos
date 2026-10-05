namespace Chronos.Service.Configuration;

public sealed class ChronosPaths
{
    public ChronosPaths(string dataDirectory, string userLogDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userLogDirectory);

        DataDirectory = dataDirectory;
        UserLogDirectory = Path.Combine(userLogDirectory, "logs");
    }

    public static ChronosPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Chronos"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chronos"));

    public string DataDirectory { get; }

    public string UserLogDirectory { get; }

    public string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public string StateFile => Path.Combine(DataDirectory, "state.json");

    /// <summary>Separate from <see cref="StateFile"/>: the backup must outlive the session and an unreadable state file.</summary>
    public string DnsBackupFile => Path.Combine(DataDirectory, "dns-backup.json");

    public string ServiceLogDirectory => Path.Combine(DataDirectory, "logs");

    public void EnsureDataDirectoryExists()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ServiceLogDirectory);
    }
}
