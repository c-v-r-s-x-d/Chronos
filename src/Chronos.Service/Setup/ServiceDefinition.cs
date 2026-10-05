namespace Chronos.Service.Setup;

/// <summary>
/// When the service manager starts the service. Chronos's own type, to keep
/// <see cref="System.ServiceProcess.ServiceStartMode"/> out of a plain data record.
/// </summary>
public enum ServiceStartMode
{
    Automatic,
    Manual,
    Disabled,
}

/// <summary>
/// How the service is registered and what the service manager does when it fails. Data only.
/// </summary>
// RestartOnNonCrashFailures is fFailureActionsOnNonCrashFailures. The platform default (FALSE)
// restarts only when the process dies without reporting SERVICE_STOPPED, but the Generic Host
// reports SERVICE_STOPPED with a non-zero exit code on an unhandled exception, so FALSE would
// leave the service down.
public sealed record ServiceDefinition(
    string Name,
    string DisplayName,
    string Description,
    string ImagePath,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<TimeSpan> RestartDelays,
    TimeSpan FailureCountResetPeriod,
    bool RestartOnNonCrashFailures = true,
    ServiceStartMode StartMode = ServiceStartMode.Automatic,
    string? AccountName = null)
{
    /// <summary>The service name. A constant because code that queries or stops the service has no image path to build a definition from.</summary>
    public const string ChronosServiceName = "ChronosService";

    public static ServiceDefinition Chronos(string imagePath) => new(
        Name: ChronosServiceName,
        DisplayName: "Chronos",
        Description: "Blocks the sites and applications you chose, for as long as you chose.",
        ImagePath: imagePath,
        Dependencies: ["Tcpip", "Dnscache"],
        RestartDelays: [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)],
        FailureCountResetPeriod: TimeSpan.FromDays(1),
        RestartOnNonCrashFailures: true);

    /// <summary>The image path, quoted so a space stays part of it.</summary>
    public string CommandLine => $"\"{ImagePath}\"";

    /// <summary>The dependency list as CreateServiceW reads it: NUL-separated, double NUL at the end.</summary>
    public string DependencyBlock => Dependencies.Count == 0
        ? "\0"
        : string.Join('\0', Dependencies) + "\0\0";

    // IReadOnlyList members compare by reference, so compare their values explicitly.
    public bool Equals(ServiceDefinition? other) =>
        other is not null
        && Name == other.Name
        && DisplayName == other.DisplayName
        && Description == other.Description
        && ImagePath == other.ImagePath
        && Dependencies.SequenceEqual(other.Dependencies)
        && RestartDelays.SequenceEqual(other.RestartDelays)
        && FailureCountResetPeriod == other.FailureCountResetPeriod
        && RestartOnNonCrashFailures == other.RestartOnNonCrashFailures
        && StartMode == other.StartMode
        && AccountName == other.AccountName;

    public override int GetHashCode()
    {
        var hash = default(HashCode);

        hash.Add(Name);
        hash.Add(DisplayName);
        hash.Add(Description);
        hash.Add(ImagePath);

        foreach (var dependency in Dependencies)
        {
            hash.Add(dependency);
        }

        foreach (var delay in RestartDelays)
        {
            hash.Add(delay);
        }

        hash.Add(FailureCountResetPeriod);
        hash.Add(RestartOnNonCrashFailures);
        hash.Add(StartMode);
        hash.Add(AccountName);

        return hash.ToHashCode();
    }

    // The generated ToString would print full paths and raw NULs; paths belong in the log only at Debug.
    public override string ToString() => $"Service {Name} ({StartMode})";
}
