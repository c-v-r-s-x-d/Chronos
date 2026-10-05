using System.Text;
using Chronos.Service.Setup;
using Result = Chronos.Service.Setup.SchtasksRegistration.ProcessResult;

namespace Chronos.Service.Tests;

/// <summary>
/// What Chronos asks schtasks to do, and what it makes of the answer. Only the last test touches the real schtasks, and only to read;
/// the rest use stand-ins for the process and the scheduler's folders.
/// </summary>
public sealed class SchtasksRegistrationTests
{
    private const string TaskName = @"Chronos\Recovery";

    /// <summary>ERROR_FILE_NOT_FOUND as an HRESULT: what a missing task answers with.</summary>
    private const int NotFound = unchecked((int)0x80070002);

    /// <summary>ERROR_ACCESS_DENIED as an HRESULT: measured for a task this account may not read.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    [Fact]
    public void ATaskIsAskedAfterByNameAndInNumbers()
    {
        // /HRESULT makes the answer a code rather than a localized sentence; without it "no such task" and "you may not look" are both exit code 1.
        var schtasks = new Schtasks();

        new SchtasksRegistration(TaskName, schtasks.Run, _ => { }).Exists();

        Assert.Equal([["/Query", "/TN", TaskName, "/HRESULT"]], schtasks.Calls);
    }

    [Fact]
    public void ATaskTheSchedulerFoundIsRegistered()
    {
        Assert.True(Registration(_ => new Result(0, string.Empty)).Exists());
    }

    [Fact]
    public void ATaskTheSchedulerHasNotGotIsNotRegistered()
    {
        Assert.False(Registration(_ => new Result(NotFound, "ERROR: The system cannot find the file specified.")).Exists());
    }

    [Theory]
    [InlineData(AccessDenied)]
    [InlineData(1)]
    [InlineData(unchecked((int)0x8004131F))]
    public void ATaskThatCouldNotBeReadIsNotATaskThatIsNotThere(int code)
    {
        // Uninstall depends on this. A task whose descriptor denies this account, an unparseable registration, or schtasks failing for its own reason all leave a task behind.
        // Reported as "not registered", uninstall would skip it and the recovery would keep firing at every boot.
        var failure = Assert.Throws<InvalidOperationException>(() => Registration(_ => new Result(code, "no")).Exists());

        Assert.Contains($"0x{code:X8}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatSchtasksSaidIsQuotedAsSchtasksWordsAndNotAsOurs()
    {
        // The product's own output is English; schtasks writes in the machine's language and the sentence goes whole into the diagnostic package.
        // The reader must tell which half is ours and which is Microsoft's. ASCII only, because source has no business carrying another language.
        var said = "FEHLER: Zugriff verweigert.";

        var failure = Assert.Throws<InvalidOperationException>(
            () => Registration(_ => new Result(AccessDenied, said)).Exists());

        Assert.Contains("schtasks said, in this machine's own language:", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith(said, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteringReplacesWhateverTaskOfThatNameIsThere()
    {
        // /F is what makes an upgrade an upgrade. Without it a Chronos installed over an older
        // Chronos fails on the task the older one registered.
        var schtasks = new Schtasks();

        new SchtasksRegistration(TaskName, schtasks.Run, _ => { }).Register("<Task />");

        var call = Assert.Single(schtasks.Calls);
        Assert.Equal(["/Create", "/TN", TaskName, "/XML", call[4], "/F", "/HRESULT"], call);
        Assert.EndsWith(".xml", call[4], StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentReachesSchtasksAsTheUtf16ItSaysItIs()
    {
        // schtasks refuses a task document that is not UTF-16 with a byte order mark, with a parse error that names the XML, not the encoding.
        // The bytes are read off the file schtasks is pointed at, since only the file can say whether the declaration is true.
        var document = RecoveryTaskDefinition.BuildXml(@"C:\Program Files\Chronos\chronos.exe");
        byte[] bytes = [];

        var schtasks = new Schtasks(arguments =>
        {
            bytes = File.ReadAllBytes(arguments[4]);

            return new Result(0, string.Empty);
        });

        new SchtasksRegistration(TaskName, schtasks.Run, _ => { }).Register(document);

        Assert.Equal([0xFF, 0xFE], bytes.Take(2));
        Assert.Equal(document, new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetString(bytes[2..]));
    }

    [Fact]
    public void TheDocumentIsGoneOnceTheTaskIsRegistered()
    {
        var file = FileSeenBy(_ => new Result(0, string.Empty), out var registration);

        registration.Register("<Task />");

        Assert.False(File.Exists(file()));
    }

    [Fact]
    public void TheDocumentIsGoneWhenSchtasksRefusedIt()
    {
        var file = FileSeenBy(_ => new Result(1, "no"), out var registration);

        Assert.Throws<InvalidOperationException>(() => registration.Register("<Task />"));
        Assert.False(File.Exists(file()));
    }

    [Fact]
    public void TheDocumentIsGoneWhenTheCallItselfFailed()
    {
        // The temporary file is deleted in a finally block; a schtasks that could not be started leaves through a different door than one that answered badly.
        var file = FileSeenBy(_ => throw new InvalidOperationException("schtasks could not be started."), out var registration);

        Assert.Throws<InvalidOperationException>(() => registration.Register("<Task />"));
        Assert.False(File.Exists(file()));
    }

    [Fact]
    public void RemovingTakesTheTaskAndThenTheFolderItLivedIn()
    {
        // schtasks deletes tasks and has no verb for folders; an uninstall that only ran /Delete would leave an empty Chronos node.
        var schtasks = new Schtasks();
        var folders = new List<string>();

        new SchtasksRegistration(TaskName, schtasks.Run, folders.Add).Remove();

        Assert.Equal(
            [["/Query", "/TN", TaskName, "/HRESULT"], ["/Delete", "/TN", TaskName, "/F", "/HRESULT"]],
            schtasks.Calls);
        Assert.Equal(["Chronos"], folders);
    }

    [Fact]
    public void AFolderLeftBehindByAnEarlierUninstallIsStillTakenAway()
    {
        // The task already gone is what a half-finished uninstall looks like; stopping there would leave the folder for good.
        var schtasks = new Schtasks(_ => new Result(NotFound, string.Empty));
        var folders = new List<string>();

        new SchtasksRegistration(TaskName, schtasks.Run, folders.Add).Remove();

        Assert.Single(schtasks.Calls);
        Assert.Equal("/Query", schtasks.Calls[0][0]);
        Assert.Equal(["Chronos"], folders);
    }

    [Fact]
    public void OnlyTheFolderTheTaskLivesInIsEverRemoved()
    {
        // A task in the root has no folder of ours, and the scheduler's root is not something to
        // hand to a delete.
        var folders = new List<string>();

        new SchtasksRegistration("Recovery", new Schtasks().Run, folders.Add).Remove();

        Assert.Empty(folders);
    }

    [Fact]
    public void TheSchedulersOwnRootIsNeverHandedToAFolderDelete()
    {
        // The scheduler prints task names with a leading separator, so "\Recovery" is "Recovery".
        // The part before the separator is empty, and the empty string is the root folder.
        var folders = new List<string>();

        new SchtasksRegistration(@"\Recovery", new Schtasks().Run, folders.Add).Remove();

        Assert.Empty(folders);
    }

    [Fact]
    public void AFolderIsNotRemovedWhenTheTaskInItCouldNotBe()
    {
        var folders = new List<string>();
        var schtasks = new Schtasks(arguments => new Result(arguments[0] == "/Delete" ? AccessDenied : 0, "no"));

        Assert.Throws<InvalidOperationException>(
            () => new SchtasksRegistration(TaskName, schtasks.Run, folders.Add).Remove());

        Assert.Empty(folders);
    }

    [Fact]
    public void SchtasksIsTheOneInTheSystemDirectory()
    {
        // Runs as LocalSystem out of an installer: a bare "schtasks.exe" would let a directory earlier in PATH decide what the name means.
        Assert.True(Path.IsPathFullyQualified(SchtasksRegistration.SchtasksPath));
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Path.GetDirectoryName(SchtasksRegistration.SchtasksPath));
        Assert.True(File.Exists(SchtasksRegistration.SchtasksPath));
    }

    [Fact]
    public void EveryWaitIsLongEnoughToBeAnAnswerAndShortEnoughToBeOne()
    {
        // The caller may be an MSI custom action with no console: a wait that never ends hangs the installation, and one in milliseconds calls a busy machine broken.
        Assert.InRange(SchtasksRegistration.CallTimeout, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));
        Assert.InRange(SchtasksRegistration.DrainTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void TheRealSchtasksSaysAnUnregisteredTaskIsNotThere()
    {
        // The one test that starts a process: a name nothing registered. schtasks reads the task store, needs no rights and leaves nothing behind.
        // It ties the exit code this class classifies to the one the real tool produces.
        Assert.False(new SchtasksRegistration($"Chronos-{Guid.NewGuid():N}").Exists());
    }

    private static SchtasksRegistration Registration(Func<string[], Result> answer) =>
        new(TaskName, new Schtasks(answer).Run, _ => { });

    /// <summary>A registration whose call remembers the document it was pointed at, so the file can be looked for after the call.</summary>
    private static Func<string> FileSeenBy(Func<string[], Result> answer, out SchtasksRegistration registration)
    {
        var file = string.Empty;

        var schtasks = new Schtasks(arguments =>
        {
            file = arguments[4];
            Assert.True(File.Exists(file), "schtasks was pointed at a document that was not written.");

            return answer(arguments);
        });

        registration = new SchtasksRegistration(TaskName, schtasks.Run, _ => { });

        return () => file;
    }

    /// <summary>schtasks as far as this class can tell: an argument vector in, an exit code out.</summary>
    private sealed class Schtasks(Func<string[], Result>? answer = null)
    {
        public List<string[]> Calls { get; } = [];

        public Result Run(string[] arguments)
        {
            Calls.Add(arguments);

            return answer?.Invoke(arguments) ?? new Result(0, string.Empty);
        }
    }
}
