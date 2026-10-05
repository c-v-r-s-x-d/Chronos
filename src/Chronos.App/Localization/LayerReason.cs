using Chronos.App.Resources;
using Chronos.Ipc;

namespace Chronos.App.Localization;

/// <summary>A blocking layer in words. The service sends identifiers; every word here is the interface's, in the screen's language.</summary>
public static class LayerReason
{
    /// <summary>The two reasons the service makes up for a layer that named none. Matched by suffix, so no line per layer.</summary>
    private const string Unhandled = ".unhandled";

    private const string Unavailable = ".unavailable";

    /// <summary>One reason in words, or the identifier itself when this build does not know it: a newer service may send reasons this build cannot, and the identifier is what a person can quote.</summary>
    public static string Text(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            // An available layer carries no reason.
            return string.Empty;
        }

        return code switch
        {
            "wfp.disabled-by-user" => Strings.LayerReasonWfpDisabledByUser,
            "wfp.engine-unavailable" => Strings.LayerReasonWfpEngineUnavailable,
            "hosts.directory-missing" => Strings.LayerReasonHostsDirectoryMissing,
            "hosts.not-writable" => Strings.LayerReasonHostsNotWritable,
            "apps.no-event-source" => Strings.LayerReasonAppsNoEventSource,
            "dns.port-busy" => Strings.LayerReasonDnsPortBusy,
            "dns.no-upstream" => Strings.LayerReasonDnsNoUpstream,
            "dns.settings-refused" => Strings.LayerReasonDnsSettingsRefused,
            "dns.backup-one-place" => Strings.LayerReasonDnsBackupOnePlace,
            "dns.loopback-blocked" => Strings.LayerReasonDnsLoopbackBlocked,
            _ when code.EndsWith(Unhandled, StringComparison.Ordinal) => Strings.LayerReasonUnhandled,
            _ when code.EndsWith(Unavailable, StringComparison.Ordinal) => Strings.LayerReasonUnavailable,
            _ => code,
        };
    }

    /// <summary>The layer's name in words, or its identifier when this build does not know it.</summary>
    public static string Name(string layer) => layer switch
    {
        "wfp" => Strings.LayerWfp,
        "hosts" => Strings.LayerHosts,
        "apps" => Strings.LayerApps,
        LayerNames.Dns => Strings.LayerDns,
        _ => layer,
    };

    /// <summary>The layer in words. During a session it "works" or not; before one it is only "available" or not.</summary>
    public static LayerLine Describe(LayerStatus status, bool blocking)
    {
        ArgumentNullException.ThrowIfNull(status);

        var state = (blocking, status.IsAvailable) switch
        {
            (true, true) => Strings.LayerAvailable,
            (true, false) => Strings.LayerNotWorking,
            (false, true) => Strings.LayerReady,
            _ => Strings.LayerUnavailable,
        };

        return new LayerLine(Name(status.Name), status.IsAvailable, state, Text(status.ReasonCode));
    }
}

/// <summary>One line of the layer list, already in words and rebuilt from the status. IsAvailable is the flag the markup uses to hide the reason.</summary>
public sealed record LayerLine(string Name, bool IsAvailable, string State, string Reason);
