using System.Reflection;
using Chronos.App.Localization;
using Chronos.Core.Enforcement;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The service sends an identifier and the interface owns the sentence; these tests cover the interface's half.</summary>
[Collection(CultureBound.Name)]
public sealed class LayerReasonTests
{
    /// <summary>Every reason the service can send, read off its constants; a copied list would agree with itself when a reason goes untranslated.</summary>
    public static TheoryData<string> EveryCode()
    {
        var codes = new TheoryData<string>();
        foreach (var code in ServiceReasonCodes.All)
        {
            codes.Add(code);
        }

        return codes;
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void EveryReasonCodeTheServiceCanProduceHasText(string code)
    {
        var text = LayerReason.Text(code);

        Assert.False(string.IsNullOrWhiteSpace(text), $"{code} has no text at all.");
        Assert.NotEqual(code, text);
    }

    /// <summary>The walk must find the known codes, or the theory above would pass by running no cases.</summary>
    [Fact]
    public void TheCodesAreReadFromTheServiceAndNotFromNothing()
    {
        var all = ServiceReasonCodes.All;

        Assert.Contains("wfp.disabled-by-user", all);
        Assert.Contains("wfp.engine-unavailable", all);
        Assert.Contains("hosts.directory-missing", all);
        Assert.Contains("hosts.not-writable", all);
        Assert.Contains("apps.no-event-source", all);
        Assert.Contains("dns.loopback-blocked", all);
        Assert.Contains("wfp.unhandled", all);
        Assert.Contains("hosts.unavailable", all);
    }

    /// <summary>An unknown identifier is shown as itself: the code is what a user can quote when asking for help.</summary>
    [Fact]
    public void AnUnknownReasonCodeIsShownAsItself()
    {
        Assert.Equal("dns.port-53-taken", LayerReason.Text("dns.port-53-taken"));
    }

    /// <summary>The suffix rules cover the two reasons the coordinator makes up for any layer, not a code that merely resembles one.</summary>
    [Fact]
    public void ACodeThatOnlyLooksLikeAKnownOneIsStillShownAsItself()
    {
        Assert.Equal("wfp.engine-unhandled-by-us", LayerReason.Text("wfp.engine-unhandled-by-us"));
    }

    /// <summary>The Russian interface reads its own wording; the platform's sentence never reaches the user.</summary>
    [Fact]
    public void AKnownReasonReadsInTheLanguageOnTheScreen()
    {
        string english;
        using (var _ = new UiCulture("en-US"))
        {
            english = LayerReason.Text("wfp.engine-unavailable");
        }

        using var russian = new UiCulture("ru-RU");

        Assert.NotEqual(english, LayerReason.Text("wfp.engine-unavailable"));
    }

    /// <summary>A VPN's filter drops the resolver's own queries; the user is told in their language, with the likely cause.</summary>
    [Fact]
    public void ALayerThatCannotHearItselfSaysSoInBothLanguagesAndNamesTheVpn()
    {
        string english;
        using (var _ = new UiCulture("en-US"))
        {
            english = LayerReason.Text("dns.loopback-blocked");
        }

        using var russian = new UiCulture("ru-RU");
        var text = LayerReason.Text("dns.loopback-blocked");

        Assert.NotEqual(english, text);
        Assert.Contains("VPN", english, StringComparison.Ordinal);
        Assert.Contains("VPN", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoReasonIsNoText(string? code)
    {
        Assert.Equal(string.Empty, LayerReason.Text(code));
    }

    [Fact]
    public void EveryLayerTheServiceDrivesHasAName()
    {
        Assert.NotEmpty(ServiceReasonCodes.Layers);

        foreach (var layer in ServiceReasonCodes.Layers)
        {
            var line = LayerReason.Describe(new LayerStatus(layer, true, null, "applied", null), blocking: true);

            Assert.False(string.IsNullOrWhiteSpace(line.Name), $"{layer} has no name.");
            Assert.NotEqual(layer, line.Name);
        }
    }

    [Fact]
    public void AnUnknownLayerIsNamedByItsIdentifier()
    {
        var line = LayerReason.Describe(new LayerStatus("vpn", false, "vpn.tunnel-down", "failed", null), blocking: true);

        Assert.Equal("vpn", line.Name);
        Assert.Equal("vpn.tunnel-down", line.Reason);
    }

    [Fact]
    public void AnAvailableLayerSaysSoAndCarriesNoReason()
    {
        var line = LayerReason.Describe(new LayerStatus("wfp", true, null, "applied", null), blocking: true);

        Assert.True(line.IsAvailable);
        Assert.Empty(line.Reason);
        Assert.False(string.IsNullOrWhiteSpace(line.State));
    }

    /// <summary>"Works" and "does not work" during a session; "available" and "unavailable" before one.</summary>
    [Fact]
    public void EachStateOfALayerHasItsOwnWord()
    {
        var words = new[] { true, false }
            .SelectMany(blocking => new[] { true, false }.Select(available =>
                LayerReason.Describe(new LayerStatus("wfp", available, available ? null : "wfp.engine-unavailable", "applied", null), blocking).State))
            .ToArray();

        Assert.Equal(4, words.Distinct().Count());
    }
}

/// <summary>The reasons the service can put on a <see cref="LayerStatus"/>, gathered from its declarations: each enforcer's <c>LayerName</c> constant and the constants beginning with it, plus the two the coordinator adds.</summary>
internal static class ServiceReasonCodes
{
    private const string LayerNameField = "LayerName";

    public static IReadOnlyList<string> Layers { get; } = ReadLayers();

    public static IReadOnlyList<string> All { get; } = ReadAll();

    private static IReadOnlyList<string> ReadLayers() =>
        [.. Enforcers().Select(LayerNameOf).Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> ReadAll()
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var enforcer in Enforcers())
        {
            var layer = LayerNameOf(enforcer);

            foreach (var field in Constants(enforcer))
            {
                if (field.Name != LayerNameField
                    && field.GetRawConstantValue() is string value
                    && value.StartsWith(layer + ".", StringComparison.Ordinal))
                {
                    codes.Add(value);
                }
            }

            codes.Add(ReconcileCoordinator.UnhandledReason(layer));
            codes.Add(ReconcileCoordinator.UnavailableReason(layer));
        }

        return [.. codes];
    }

    private static IEnumerable<Type> Enforcers() =>
        typeof(Chronos.Service.Sites.HostsEnforcer).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IEnforcer).IsAssignableFrom(type));

    private static string LayerNameOf(Type enforcer)
    {
        var field = Constants(enforcer).FirstOrDefault(candidate => candidate.Name == LayerNameField);
        Assert.True(field is not null, $"{enforcer.Name} names no layer.");

        var name = field!.GetRawConstantValue() as string;
        Assert.False(string.IsNullOrEmpty(name), $"{enforcer.Name}.LayerName is empty.");

        return name!;
    }

    private static IEnumerable<FieldInfo> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string));
}
