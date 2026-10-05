using System.Reflection;
using System.Xml.Linq;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

public sealed class RecoveryTaskDefinitionTests
{
    private const string Command = @"C:\Program Files\Chronos\chronos.exe";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Xml() => XDocument.Parse(RecoveryTaskDefinition.BuildXml(Command));

    private static string Value(XDocument document, string name) =>
        document.Descendants(Ns + name).Single().Value;

    [Fact]
    public void ItRunsWhenTheSystemBoots()
    {
        // A task with no boot trigger never runs on the machine that needs it.
        Assert.Single(Xml().Descendants(Ns + "BootTrigger"));
    }

    [Fact]
    public void ItRunsAsTheSystemAccountByItsWellKnownSid()
    {
        // S-1-5-18 rather than a name, which is localized. The procedure deletes filters and rewrites the hosts file, which a user account may not do.
        Assert.Equal("S-1-5-18", Value(Xml(), "UserId"));
    }

    [Fact]
    public void NeitherBatteryNorIdleCanKeepItFromRunning()
    {
        // The default for both is "yes, they can": a laptop booting on battery into a broken installation is the target.
        var xml = Xml();
        Assert.Equal("false", Value(xml, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(xml, "StopIfGoingOnBatteries"));
        Assert.Equal("false", Value(xml, "RunOnlyIfIdle"));
    }

    [Fact]
    public void TheTaskIsEnabledAndIsNotStoppedWhenTheMachineStopsBeingIdle()
    {
        // A task registered with Enabled false never fires, so the document says so.
        // StopOnIdleEnd true would have the scheduler kill the recovery when somebody touches the keyboard.
        var xml = Xml();
        Assert.Equal("true", Value(xml, "Enabled"));
        Assert.Equal("false", Value(xml, "StopOnIdleEnd"));
    }

    [Fact]
    public void ItRunsWithTheHighestRightsTheAccountHas()
    {
        // Without this the task runs filtered and deleting WFP filters and rewriting the hosts file are refused, which as SYSTEM looks like anything but a rights problem.
        Assert.Equal("HighestAvailable", Value(Xml(), "RunLevel"));
    }

    [Fact]
    public void ItRunsTheRecoverCommand()
    {
        var xml = Xml();
        Assert.Equal(Command, Value(xml, "Command"));
        Assert.Equal("recover", Value(xml, "Arguments"));
    }

    [Fact]
    public void ItIsAllowedLongerThanTheProcedureMayTake()
    {
        // The procedure may wait up to two minutes for the service; a shorter time limit would have the scheduler kill it mid-wait.
        Assert.Equal("PT5M", Value(Xml(), "ExecutionTimeLimit"));
    }

    [Fact]
    public void TheTaskLivesInItsOwnFolder()
    {
        Assert.Equal(@"Chronos\Recovery", RecoveryTaskDefinition.TaskName);
    }

    [Fact]
    public void ItDeclaresTheEncodingTheFileIsWrittenIn()
    {
        // schtasks refuses a UTF-8 file, so the file is UTF-16 and the declaration must say so.
        Assert.StartsWith(
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>",
            RecoveryTaskDefinition.BuildXml(Command),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchedulerItselfAcceptsTheDocument()
    {
        // This asks the scheduler, which has the last word and validates far more than well-formedness: an unknown element, a missing Actions block,
        // an unknown version, a malformed duration, a priority out of range, a Principal id Actions Context does not name.
        // 1.2 is the lowest version with MultipleInstancesPolicy. The second call is the control: a parser accepting anything would otherwise pass.
        Assert.Null(Parse(RecoveryTaskDefinition.BuildXml(Command)));
        Assert.NotNull(Parse(RecoveryTaskDefinition.BuildXml(Command)
            .Replace("<BootTrigger />", "<BootTrigger /><WhenTheMoonIsFull />", StringComparison.Ordinal)));
    }

    /// <summary>The document through the scheduler's own parser: null when it took it, the complaint when it did not. Nothing is registered and no elevation is needed.</summary>
    private static Exception? Parse(string document)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        var service = Activator.CreateInstance(type)!;

        // Connect's four arguments name another machine and an account; missing means this machine as this process.
        type.InvokeMember(
            "Connect",
            BindingFlags.InvokeMethod,
            binder: null,
            service,
            [Type.Missing, Type.Missing, Type.Missing, Type.Missing]);

        var task = type.InvokeMember("NewTask", BindingFlags.InvokeMethod, binder: null, service, [0u])!;

        try
        {
            task.GetType().InvokeMember("XmlText", BindingFlags.SetProperty, binder: null, task, [document]);

            return null;
        }
        catch (TargetInvocationException exception)
        {
            return exception.InnerException;
        }
    }

    [Fact]
    public void APathTheInstallerChoseIsEscapedAsXml()
    {
        // The path comes from the installer and may contain an ampersand; unescaped it makes a file the scheduler rejects as malformed, with an error that names the XML, not the directory.
        const string awkward = @"D:\Tools & Toys\Chronos <2>\chronos.exe";

        var xml = XDocument.Parse(RecoveryTaskDefinition.BuildXml(awkward));

        Assert.Equal(awkward, xml.Descendants(Ns + "Command").Single().Value);
    }
}
