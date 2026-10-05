using System.Security;

namespace Chronos.Service.Setup;

/// <summary>
/// The XML for the scheduled task that runs the recovery procedure at boot. A pure function of the
/// path to chronos.exe, so tests can check it without registering anything. Built as text so the
/// declaration and node order match what the scheduler itself writes.
/// </summary>
public static class RecoveryTaskDefinition
{
    /// <summary>The task name, in a folder of its own. schtasks deletes only tasks, so SchtasksRegistration.Remove removes the folder too.</summary>
    public const string TaskName = @"Chronos\Recovery";

    public const string Arguments = "recover";

    /// <summary>LocalSystem by SID, since its name is localised. The procedure deletes filters and rewrites the hosts file.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>Five minutes; the procedure may wait two for the service, and a shorter limit would kill it mid-wait.</summary>
    public const string ExecutionTimeLimit = "PT5M";

    /// <summary>Builds the task document for a Chronos CLI at <paramref name="commandPath"/>.</summary>
    public static string BuildXml(string commandPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandPath);

        // Escaped: an ampersand is legal in a Windows path but malformed in XML.
        var command = SecurityElement.Escape(commandPath);

        // The UTF-16 declaration must match the file encoding; schtasks refuses anything else.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Restores this machine to its unblocked state when the Chronos service cannot run.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger />
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SystemSid}</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>{ExecutionTimeLimit}</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{Arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
