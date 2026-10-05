using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Service.Sites;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

public sealed class HostsEnforcerTests : IDisposable
{
    private const string Foreign = "127.0.0.1 localhost\r\n# a comment of someone else\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDnsCache _dns = new();
    private readonly CapturingLogger<HostsEnforcer> _logger = new();

    public HostsEnforcerTests() => Directory.CreateDirectory(_root);

    private string HostsPath => Path.Combine(_root, "hosts");

    private HostsEnforcer Create() => new(new HostsFile(HostsPath), _dns, _logger);

    private static EnforcementPlan Plan(params string[] domains) => new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddHours(1),
        [.. domains.Select(domain => new SiteRule(domain, includeSubdomains: true))],
        []);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WritesTheBlockForThePlan()
    {
        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var content = File.ReadAllText(HostsPath);
        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Contains("0.0.0.0 example.com", content, StringComparison.Ordinal);
        Assert.Contains(":: www.example.com", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_FlushesTheResolverCacheAfterWriting()
    {
        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(1, _dns.FlushCount);
    }

    [Fact]
    public async Task Reconcile_TouchesNothingWhenThePlanIsAlreadyApplied()
    {
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        // Making the file read-only turns any write attempt into a failure instead of a
        // difference a later assertion might miss.
        File.SetAttributes(HostsPath, FileAttributes.ReadOnly);
        try
        {
            var second = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

            Assert.Equal(ReconcileOutcome.Unchanged, second.Outcome);
            Assert.Equal(1, _dns.FlushCount);
            // A repeated pass adds no Information entries: a log that grows every minute of a long session is unreadable.
            Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Information);
        }
        finally
        {
            File.SetAttributes(HostsPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Reconcile_KeepsExistingEntriesWhenADomainIsAdded()
    {
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var result = await enforcer.ReconcileAsync(Plan("example.com", "other.org"), CancellationToken.None);

        var content = File.ReadAllText(HostsPath);
        Assert.Contains("0.0.0.0 example.com", content, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0 other.org", content, StringComparison.Ordinal);
        // Two names per domain: the domain itself and its www variant.
        Assert.Equal(2, result.Applied);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public async Task Reconcile_ReportsRemovedNames()
    {
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com", "other.org"), CancellationToken.None);

        var result = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.Removed);
    }

    [Fact]
    public async Task Reconcile_RemovesTheBlockWhenThePlanHasNoSites()
    {
        var enforcer = Create();
        File.WriteAllText(HostsPath, Foreign);
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        await enforcer.ReconcileAsync(EnforcementPlan.Empty, CancellationToken.None);

        Assert.Equal(Foreign, File.ReadAllText(HostsPath));
    }

    [Fact]
    public async Task Reconcile_RestoresTheBlockAfterItWasDeletedByHand()
    {
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        File.WriteAllText(HostsPath, Foreign);

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Contains("0.0.0.0 example.com", File.ReadAllText(HostsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyThenClear_LeavesForeignContentByteForByte()
    {
        var enforcer = Create();
        File.WriteAllText(HostsPath, Foreign);

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        await enforcer.ClearAsync(CancellationToken.None);

        Assert.Equal(Foreign, File.ReadAllText(HostsPath));
    }

    [Fact]
    public async Task ApplyThenClear_LeavesNonAsciiForeignContentByteForByte()
    {
        // Bytes, not characters: "# блок" in cp1251 must come back unharmed.
        byte[] foreign = [0x23, 0x20, 0xE1, 0xEB, 0xEE, 0xEA, 0x0D, 0x0A, 0x31, 0x32, 0x37, 0x0D, 0x0A];
        File.WriteAllBytes(HostsPath, foreign);
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        await enforcer.ClearAsync(CancellationToken.None);

        Assert.Equal(foreign, File.ReadAllBytes(HostsPath));
    }

    [Fact]
    public async Task Reconcile_SkipsNonAsciiNamesAndReportsHowManyAtDebug()
    {
        // The file is written as Latin1 so foreign bytes survive; a name outside ASCII cannot be written
        // back as itself, and punycode (IDN) is not supported.
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("пример.рф", "example.com"), CancellationToken.None);

        var content = File.ReadAllText(HostsPath);
        Assert.Contains("0.0.0.0 example.com", content, StringComparison.Ordinal);
        Assert.DoesNotContain("пример", content, StringComparison.Ordinal);
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Debug && entry.Message.Contains("Skipped 1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            _logger.Entries,
            entry => entry.Level >= LogLevel.Information && entry.Message.Contains("пример", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clear_DoesNothingWhenThereIsNoBlock()
    {
        File.WriteAllText(HostsPath, Foreign);

        await Create().ClearAsync(CancellationToken.None);

        Assert.Equal(0, _dns.FlushCount);
    }

    [Fact]
    public async Task Clear_DoesNothingWhenTheFileIsMissing()
    {
        await Create().ClearAsync(CancellationToken.None);

        Assert.False(File.Exists(HostsPath));
    }

    [Fact]
    public async Task Reconcile_ReportsAStableCodeAndLeavesTheFileIntactWhenTheWriteFails()
    {
        // The reason travels to a bilingual UI, so a failed reconcile must report the same kind of code
        // ProbeAsync does, not the English message of the exception. A failed write must leave the file as it was.
        File.WriteAllText(HostsPath, Foreign);
        File.SetAttributes(HostsPath, FileAttributes.ReadOnly);

        try
        {
            var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

            Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
            Assert.Equal(HostsEnforcer.NotWritable, result.Detail);
            Assert.NotNull(result.Error);
            Assert.Equal(Foreign, File.ReadAllText(HostsPath));
            Assert.Equal([HostsPath], Directory.GetFiles(_root));
        }
        finally
        {
            File.SetAttributes(HostsPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Probe_ReportsAvailableWhenTheFileCanBeWritten()
    {
        File.WriteAllText(HostsPath, Foreign);

        var availability = await Create().ProbeAsync(CancellationToken.None);

        Assert.True(availability.IsAvailable);
    }

    [Fact]
    public async Task Probe_ReportsAvailableWhenTheFileDoesNotExistYet()
    {
        var availability = await Create().ProbeAsync(CancellationToken.None);

        Assert.True(availability.IsAvailable);
    }

    [Fact]
    public async Task Probe_ReportsACodeWhenSomethingElseHoldsTheFile()
    {
        // Antivirus products lock the hosts file to stop exactly what this layer does.
        File.WriteAllText(HostsPath, Foreign);
        using var exclusive = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var availability = await Create().ProbeAsync(CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(HostsEnforcer.NotWritable, availability.Reason);
    }

    [Fact]
    public async Task Probe_ReportsACodeWhenTheDirectoryIsGone()
    {
        var enforcer = new HostsEnforcer(
            new HostsFile(Path.Combine(_root, "missing", "hosts")), _dns, _logger);

        var availability = await enforcer.ProbeAsync(CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(HostsEnforcer.DirectoryMissing, availability.Reason);
    }

    [Fact]
    public async Task NoDomainReachesTheLogAboveDebug()
    {
        // The service log must not become a browsing history.
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        await enforcer.ClearAsync(CancellationToken.None);

        Assert.DoesNotContain(
            _logger.Entries,
            entry => entry.Level >= LogLevel.Information
                && entry.Message.Contains("example.com", StringComparison.OrdinalIgnoreCase));
    }
}
