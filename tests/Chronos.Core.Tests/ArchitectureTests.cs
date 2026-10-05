using System.Xml.Linq;

namespace Chronos.Core.Tests;

public sealed class ArchitectureTests
{
    private const string CoreProject = "src/Chronos.Core/Chronos.Core.csproj";

    [Fact]
    public void CoreProject_TargetsPlatformNeutralFramework()
    {
        var project = XDocument.Load(RepoPath.Resolve(CoreProject));

        var targetFramework = project.Descendants("TargetFramework").Single().Value;

        Assert.Equal("net10.0", targetFramework);
    }

    [Fact]
    public void CoreProject_HasNoPlatformSpecificReferences()
    {
        var project = XDocument.Load(RepoPath.Resolve(CoreProject));

        var packages = project.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")!.Value)
            .ToList();
        Assert.DoesNotContain(packages, name => name.Contains("Windows", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));

        Assert.DoesNotContain(
            project.Descendants("UseWPF"),
            element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            project.Descendants("UseWindowsForms"),
            element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CoreProject_OnlySystemClockReadsSystemTime()
    {
        var forbidden = new[] { "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow" };
        var allowedFile = Path.Combine("Time", "SystemClock.cs");
        var separator = Path.DirectorySeparatorChar;

        var root = RepoPath.Resolve("src/Chronos.Core");
        var sourceFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal));

        foreach (var file in sourceFiles)
        {
            if (file.EndsWith(allowedFile, StringComparison.Ordinal))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            foreach (var pattern in forbidden)
            {
                Assert.False(
                    content.Contains(pattern, StringComparison.Ordinal),
                    $"{file} reads system time directly via '{pattern}'; route it through IClock instead.");
            }
        }
    }
}
