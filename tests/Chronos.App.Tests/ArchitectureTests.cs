using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chronos.App.Services;

namespace Chronos.App.Tests;

/// <summary>The interface changes nothing on the machine itself: it references neither the service nor the domain, so every change travels down the pipe.</summary>
public sealed class ArchitectureTests
{
    private const string App = "Chronos.App";

    private const string Tests = "Chronos.App.Tests";

    private static readonly string[] Forbidden = ["Chronos.Core", "Chronos.Service"];

    [Fact]
    public void TheInterfaceDoesNotReferenceTheServiceOrTheDomain()
    {
        var reached = CompiledReferences(typeof(ServiceLink).Assembly);

        Assert.Contains("Chronos.Ipc", reached);
        foreach (var name in Forbidden)
        {
            Assert.DoesNotContain(name, reached);
        }
    }

    /// <summary>Proves the check above can fail: this assembly references the service on purpose, and the walk finds it.</summary>
    [Fact]
    public void TheCheckNoticesAReferenceWhereThereIsOne()
    {
        var reached = CompiledReferences(typeof(ArchitectureTests).Assembly);

        Assert.Contains("Chronos.Service", reached);
    }

    /// <summary>The same boundary read from the build. An unused project reference emits no assembly reference, so it would slip past <see cref="CompiledReferences"/>.</summary>
    [Fact]
    public void TheInterfaceProjectIsBuiltAgainstNothingButTheProtocol()
    {
        var declared = DeclaredDependencies(App);

        Assert.Contains("Chronos.Ipc", declared);
        foreach (var name in Forbidden)
        {
            Assert.DoesNotContain(name, declared);
        }

        // Self-check: this project declares the service, and the reader sees it.
        Assert.Contains("Chronos.Service", DeclaredDependencies(Tests));
    }

    /// <summary>View models do not know Avalonia exists. Read off the files, since Chronos.App references Avalonia either way; this keeps the suite free of a windowing system.</summary>
    [Fact]
    public void TheViewModelsDoNotKnowAvaloniaExists()
    {
        var offenders = Mentioning("Avalonia", Source("ViewModels")).ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>Proves the check above can fail: the views know Avalonia and the same scan finds it there.</summary>
    [Fact]
    public void TheCheckNoticesAvaloniaWhereItBelongs()
    {
        Assert.NotEmpty(Mentioning("Avalonia", Source("Views")).ToArray());
    }

    private static IEnumerable<string> Mentioning(string word, string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => Code(File.ReadAllText(file)).Contains(word, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Select(name => name ?? string.Empty);

    /// <summary>The file with its comments taken out, so prose about Avalonia is not mistaken for a using directive.</summary>
    private static string Code(string file) =>
        Regex.Replace(Regex.Replace(file, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//.*$", string.Empty, RegexOptions.Multiline);

    private static string Source(string folder)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chronos.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "The solution root is not above the build output.");

        var source = Path.Combine(directory!.FullName, "src", "Chronos.App", folder);
        Assert.True(Directory.Exists(source), $"{source} is not there.");

        return source;
    }

    /// <summary>Every assembly name the closure mentions. Only Chronos assemblies are opened to look further.</summary>
    private static HashSet<string> CompiledReferences(Assembly root)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>();
        pending.Enqueue(root);

        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name is null || !reached.Add(name) || !name.StartsWith("Chronos.", StringComparison.Ordinal))
                {
                    continue;
                }

                pending.Enqueue(Assembly.Load(reference));
            }
        }

        return reached;
    }

    private static HashSet<string> DeclaredDependencies(string library)
    {
        var deps = Path.Combine(AppContext.BaseDirectory, $"{Tests}.deps.json");
        Assert.True(File.Exists(deps), $"{deps} is missing; the boundary cannot be read from the build.");

        using var document = JsonDocument.Parse(File.ReadAllText(deps));
        var target = document.RootElement.GetProperty("targets").EnumerateObject().Last().Value;

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(library);

        while (pending.TryDequeue(out var current))
        {
            var entry = target.EnumerateObject()
                .FirstOrDefault(property => Name(property.Name).Equals(current, StringComparison.Ordinal));

            if (entry.Value.ValueKind != JsonValueKind.Object
                || !entry.Value.TryGetProperty("dependencies", out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (reached.Add(dependency.Name) && dependency.Name.StartsWith("Chronos.", StringComparison.Ordinal))
                {
                    pending.Enqueue(dependency.Name);
                }
            }
        }

        return reached;
    }

    private static string Name(string library) => library.Split('/')[0];
}
