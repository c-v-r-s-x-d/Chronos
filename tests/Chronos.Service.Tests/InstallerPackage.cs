using System.Reflection;

namespace Chronos.Service.Tests;

/// <summary>
/// The MSI this repository builds, read out of its own tables rather than installed, so the test
/// run changes nothing on the machine. The reader is the installer's own COM object, so what the
/// tests see is what msiexec sees. Late binding: the type library has no NuGet package behind it.
/// </summary>
internal static class InstallerPackage
{
    /// <summary>The one summary property these tests care about: the package's platform and language.</summary>
    public const int SummaryTemplate = 7;

    /// <summary>The database is opened once and never closed: it is read-only, and re-opening per query would cost seconds of COM.</summary>
    private static readonly Lazy<object?> Opened = new(OpenDatabase);

    private static readonly Lazy<string?> Found = new(FindPackage);

    /// <summary>One reader at a time: xUnit runs classes in parallel, and one COM object driven from two threads flakes.</summary>
    private static readonly object Reading = new();

    /// <summary>The built package, or null when nobody has built one.</summary>
    public static string? Location => Found.Value;

    /// <summary>Every row a query answers, each row as its fields in the order they were asked for.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Query(string sql)
    {
        var database = Opened.Value ?? throw new InvalidOperationException("There is no built MSI to read.");

        lock (Reading)
        {
            var view = Call(database, "OpenView", sql);
            try
            {
                Call(view, "Execute");

                var rows = new List<IReadOnlyList<string>>();
                while (Fetch(view) is { } record)
                {
                    var count = (int)Read(record, "FieldCount")!;
                    var fields = new List<string>(count);
                    for (var field = 1; field <= count; field++)
                    {
                        // Null for an empty cell, which is how MSI stores a condition nobody wrote.
                        fields.Add((string?)Read(record, "StringData", field) ?? string.Empty);
                    }

                    rows.Add(fields);
                }

                return rows;
            }
            finally
            {
                Call(view, "Close");
            }
        }
    }

    /// <summary>One value out of the summary stream, by its property number.</summary>
    public static string Summary(int property)
    {
        var database = Opened.Value ?? throw new InvalidOperationException("There is no built MSI to read.");

        lock (Reading)
        {
            // Zero: opened for reading, with no room reserved for new properties.
            var summary = Read(database, "SummaryInformation", 0)!;

            return (string?)Read(summary, "Property", property) ?? string.Empty;
        }
    }

    /// <summary>Whether the package has a table of this name at all.</summary>
    /// <remarks>An absent table is an answer, not an error: a package that removes no files has no RemoveFile table, and querying it throws.</remarks>
    public static bool HasTable(string name) =>
        Query("SELECT `Name` FROM `_Tables`").Any(row => string.Equals(row[0], name, StringComparison.Ordinal));

    /// <summary>Where the package puts a file, as a path under the install folder: empty for the install folder itself, null when the package does not carry the file.</summary>
    /// <remarks>
    /// Walks File to component to directory to parent, the only way to tell a package that carries
    /// System.Diagnostics.EventLog.Messages.dll in runtimes\win\lib\net10.0 (where the registry entry
    /// written by EventLog.CreateEventSource will point) from one that carries it elsewhere.
    /// </remarks>
    public static string? DirectoryOf(string fileName)
    {
        var directory = Query(
                "SELECT `File`.`FileName`, `Component`.`Directory_` "
                + "FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component`")
            .Where(row => LongName(row[0]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(row => row[1])
            .FirstOrDefault();

        if (directory is null)
        {
            return null;
        }

        var parents = Query("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`")
            .ToDictionary(row => row[0], row => (Parent: row[1], Name: row[2]), StringComparer.Ordinal);

        var names = new List<string>();
        while (!string.Equals(directory, "INSTALLFOLDER", StringComparison.Ordinal))
        {
            if (!parents.TryGetValue(directory, out var entry))
            {
                // Outside the install folder: the Start Menu, or a standard directory of Windows.
                return null;
            }

            names.Insert(0, LongName(entry.Name));
            directory = entry.Parent;
        }

        return string.Join('\\', names);
    }

    /// <summary>The long name out of a name cell: MSI writes "short|long" and "target:source"; the long target is what these tests mean.</summary>
    private static string LongName(string cell)
    {
        var target = cell.Split(':')[0];
        var separator = target.IndexOf('|', StringComparison.Ordinal);

        return separator < 0 ? target : target[(separator + 1)..];
    }

    private static object? OpenDatabase()
    {
        if (Found.Value is not { } path)
        {
            return null;
        }

        var type = Type.GetTypeFromProgID("WindowsInstaller.Installer", throwOnError: true)!;
        var installer = Activator.CreateInstance(type)!;

        // Zero is read-only; anything else would take a write lock on the build output and let a test change the package.
        return Call(installer, "OpenDatabase", path, 0);
    }

    /// <summary>The newest Chronos.msi the installer project has produced, whichever configuration built it.</summary>
    /// <remarks>Searched under bin only: WiX leaves a copy in obj too, which is a build intermediate. Found by walking up from the test assembly, since the working directory is not promised.</remarks>
    private static string? FindPackage()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var output = Path.Combine(directory.FullName, "src", "Chronos.Installer", "bin");
            if (!Directory.Exists(output))
            {
                continue;
            }

            return Directory.EnumerateFiles(output, "Chronos.msi", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        return null;
    }

    private static object Call(object target, string member, params object?[] arguments) =>
        target.GetType().InvokeMember(
            member, BindingFlags.InvokeMethod, binder: null, target, arguments.Length == 0 ? null : arguments)!;

    /// <summary>The end of a result set is a null record, which <see cref="Call"/> will not return.</summary>
    private static object? Fetch(object view) =>
        view.GetType().InvokeMember("Fetch", BindingFlags.InvokeMethod, binder: null, view, args: null);

    private static object? Read(object target, string member, params object?[] arguments) =>
        target.GetType().InvokeMember(
            member, BindingFlags.GetProperty, binder: null, target, arguments.Length == 0 ? null : arguments);
}
