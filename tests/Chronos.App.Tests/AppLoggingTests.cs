using System.Globalization;
using System.Text.RegularExpressions;
using Chronos.App.Diagnostics;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.App.ViewModels;
using Chronos.Core.Rules;
using Chronos.Ipc;
using Chronos.Service.Rules;

namespace Chronos.App.Tests;

/// <summary>The interface's own log: what it records, what it must keep out, retention.</summary>
[Collection(CultureBound.Name)]
public sealed class AppLoggingTests : IAsyncLifetime
{
    /// <summary>Anything outside plain ASCII counts as not English.</summary>
    private static readonly Regex NotEnglish = new(@"[^\x00-\x7F]", RegexOptions.CultureInvariant);

    private readonly ServiceHarness _service = new();

    private readonly RecordedWaits _waits = new();

    private readonly TempLog _log = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _log.Dispose();
    }

    [Fact]
    public async Task ALogLineIsEnglishEvenWhenTheInterfaceSpeaksRussian()
    {
        using var culture = new UiCulture("ru-RU", formatting: "ru-RU");

        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync, _log.Log);
        using var shell = new ShellViewModel(link, new Text(new LanguageSwitch()), new SystemClock(), new FakeTicker());

        link.Start();

        await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Unavailable, "reported the service missing");

        Assert.Matches(NotEnglish, shell.ServiceMessage);

        var record = Assert.Single(await _log.LinesAsync(1));
        Assert.DoesNotMatch(NotEnglish, record);
        Assert.Contains("The service cannot be reached", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFullPathReachesTheLogAtInformationOrAbove()
    {
        var rule = ProtectedPath();

        // The service's reason quotes nothing.
        Assert.Equal(IpcCodes.RulesAppProtectedSystemDirectory, ServiceReasonFor(rule));

        await RefuseAsync(new IpcRequest { Command = "AddAppRule", App = rule });

        var directory = Path.GetDirectoryName(rule.Value)!;
        var above = _log.Lines().Where(line => !line.Contains("[DBG]", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(above);

        foreach (var line in above)
        {
            Assert.DoesNotContain(rule.Value, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory, line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheNameOfTheProgramIsStillThereBecauseThePersonPutItInTheirOwnList()
    {
        var rule = ProtectedPath();

        await RefuseAsync(new IpcRequest { Command = "AddAppRule", App = rule });

        var record = Assert.Single(_log.Lines());

        Assert.Contains("AddAppRule", record, StringComparison.Ordinal);
        Assert.Contains("solitaire.exe", record, StringComparison.Ordinal);

        // The code is English and is what a reader searches for.
        Assert.Contains(IpcCodes.RulesAppProtectedSystemDirectory, record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADomainFromARefusedAttemptNeverReachesTheLog()
    {
        _service.Start();

        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync, _log.Log);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Blocked += (_, block) => arrived.TrySetResult();

        link.Start();
        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        var status = new StatusPayload(
            "Active",
            ServiceHarness.Moment,
            ServiceHarness.Moment,
            ServiceHarness.Moment + TimeSpan.FromMinutes(42),
            null,
            30,
            [],
            [],
            [new LayerStatus("dns", true, null, "applied", ServiceHarness.Moment)]);
        _service.Events.Publish(IpcEvent.SiteBlocked(status, "attempted-only.example"));

        var settled = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(settled, arrived.Task), "The refused site never reached the interface.");

        // The link's own line proves the file is being written.
        await _log.LinesAsync(1);
        Assert.DoesNotContain("attempted-only.example", _log.Text(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADomainFromThePersonsOwnListIsNamedInTheRefusal()
    {
        await RefuseAsync(new IpcRequest
        {
            Command = "RemoveSiteRule",
            Site = new SiteRuleMessage("nothing-here.example", true),
        });

        var record = Assert.Single(_log.Lines());

        Assert.Contains("RemoveSiteRule", record, StringComparison.Ordinal);
        Assert.Contains("nothing-here.example", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LosingTheServiceAndFindingItAgainAreBothRecordedOnce()
    {
        _service.Start();

        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync, _log.Log);
        link.Start();

        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        await _service.StopAsync();
        await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Unavailable, "noticed the service leaving");

        _service.Start();
        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reconnected on its own");

        // Many failed attempts in between; the link never waited on wall time.
        Assert.NotEmpty(_waits.Asked);

        Assert.Equal(["found", "lost", "found"], (await _log.LinesAsync(3)).Select(Story).ToArray());
    }

    [Fact]
    public void AnUnhandledExceptionIsRecordedAtErrorWithWhatThrewIt()
    {
        _log.Log.Unhandled("the process", new InvalidOperationException("the window would not open"));

        var record = Assert.Single(_log.Lines());

        Assert.Contains("[ERR]", record, StringComparison.Ordinal);
        Assert.Contains("the process", record, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", _log.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLanguageChangeIsRecordedByItsCodeRatherThanItsName()
    {
        using var culture = new UiCulture("en-US");

        new LanguageSwitch(_log.Log).Use("ru");

        var record = Assert.Single(_log.Lines());

        Assert.DoesNotMatch(NotEnglish, record);
        Assert.Contains("en-US", record, StringComparison.Ordinal);
        Assert.Contains("ru", record, StringComparison.Ordinal);
    }

    /// <summary>CurrentCulture is left alone while CurrentUICulture moves. ar-SA reads 1448 for 2026, so a leaked machine culture shows.</summary>
    [Fact]
    public void TheTimestampDoesNotFollowTheMachinesCalendar()
    {
        using var culture = new UiCulture("ru-RU", formatting: "ar-SA");

        var gregorian = DateTimeOffset.Now.ToString("yyyy", CultureInfo.InvariantCulture);

        // Premise: on this machine the two really differ.
        Assert.NotEqual(gregorian, DateTimeOffset.Now.ToString("yyyy", CultureInfo.CurrentCulture));

        _log.Log.Started();

        Assert.StartsWith(gregorian, Assert.Single(_log.Lines()), StringComparison.Ordinal);
    }

    /// <summary>Old days are created by hand; the sweep runs when the log is opened.</summary>
    [Fact]
    public void TheLogIsKeptByTheDayAndOnlyForSevenOfThem()
    {
        Directory.CreateDirectory(_log.Directory);

        for (var day = 1; day <= 9; day++)
        {
            File.WriteAllText(Path.Combine(_log.Directory, $"app-2020010{day}.log"), "an old day\n");
        }

        _log.Log.Started();

        var files = _log.Files();

        Assert.Equal(7, files.Count);
        Assert.Contains($"app-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log", files);
        Assert.DoesNotContain("app-20200101.log", files);
    }

    /// <summary>Driven, not read off the configuration, so a sink given none of the limits fails.</summary>
    [Fact]
    public void TheLogRollsOnceItPassesTheSizeLimitRatherThanGrowingForever()
    {
        var fat = new InvalidOperationException(new string('x', 1_000_000));

        for (var i = 0; i < 12; i++)
        {
            _log.Log.Unhandled("a day that went badly", fat);
        }

        Assert.True(
            _log.Files().Count > 1,
            $"Twelve megabytes went into one file: {string.Join(", ", _log.Files())}.");
    }

    /// <summary>The service logs under %ProgramData% as another account; the interface writes under the user's local data.</summary>
    [Fact]
    public void TheInterfacesLogLivesUnderTheUsersLocalDataAndNotBesideTheServices()
    {
        var mine = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.StartsWith(mine, AppLogging.DefaultDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shared, AppLogging.DefaultDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Chronos", "logs"), AppLogging.DefaultDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALogHasToBeToldWhereToWrite()
    {
        Assert.Throws<ArgumentNullException>(() => AppLogging.Open(null!));
        Assert.Throws<ArgumentException>(() => AppLogging.Open("   "));
    }

    private static string Story(string line) =>
        line.Contains("The service answered", StringComparison.Ordinal) ? "found"
            : line.Contains("The service cannot be reached", StringComparison.Ordinal) ? "lost"
                : line;

    /// <summary>A program inside a Windows system directory, which the service will not block.</summary>
    private static AppRuleMessage ProtectedPath() =>
        new("FullPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "solitaire.exe"));

    private static string ServiceReasonFor(AppRuleMessage rule) =>
        new WindowsProtectedAppPolicy().Evaluate(new AppRule(AppMatchKind.FullPath, rule.Value)).Reason!;

    /// <summary>
    /// Sends one command to a real service and asserts it was refused.
    /// The link is never started: a subscription would add lines to the file.
    /// </summary>
    private async Task RefuseAsync(IpcRequest request)
    {
        _service.Start();

        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync, _log.Log);

        var answer = await link.SendAsync(request, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.False(answer.Accepted);
    }
}

/// <summary>A per-test log directory, deleted afterwards. Opened lazily so tests can seed old files.</summary>
internal sealed class TempLog : IDisposable
{
    private AppLog? _log;

    public string Directory { get; } =
        Path.Combine(Path.GetTempPath(), "chronos-app-log-tests", Guid.NewGuid().ToString("N"));

    public AppLog Log => _log ??= AppLogging.Open(Directory);

    public IReadOnlyList<string> Files() =>
        System.IO.Directory.Exists(Directory)
            ? [.. System.IO.Directory.EnumerateFiles(Directory)
                .Select(file => Path.GetFileName(file))
                .Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Everything written so far. The sink is opened shared and flushes each record, so it can be read while open.</summary>
    public string Text() =>
        string.Concat(Files().Select(name => Read(Path.Combine(Directory, name))));

    /// <summary>One line per record; only the first line of a multi-line record carries the timestamp.</summary>
    public IReadOnlyList<string> Lines() =>
        [.. Text().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 4 && char.IsAsciiDigit(line[0]) && line[4] == '-')];

    /// <summary>
    /// Waits until the log holds at least this many records. A link notifies subscribers before
    /// it writes its line, so a woken test is briefly ahead of the file.
    /// </summary>
    public async Task<IReadOnlyList<string>> LinesAsync(int atLeast, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));

        while (true)
        {
            var lines = Lines();
            if (lines.Count >= atLeast)
            {
                return lines;
            }

            if (DateTime.UtcNow > limit)
            {
                Assert.Fail($"The log never reached {atLeast} records; it holds {lines.Count}.");
            }

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        _log?.Dispose();

        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static string Read(string file)
    {
        using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
