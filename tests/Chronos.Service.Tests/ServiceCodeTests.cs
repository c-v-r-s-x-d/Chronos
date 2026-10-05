using System.Reflection;
using System.Text.RegularExpressions;
using Chronos.Cli;
using Chronos.Core.Sessions;
using Chronos.Ipc;

namespace Chronos.Service.Tests;

/// <summary>Refusals and warnings travel as codes and every reader owns its words. These are the service's and the CLI's halves; the interface's are in its own tests.</summary>
public sealed class ServiceCodeTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Read off the constants, so a code added tomorrow is checked without anyone listing it.</summary>
    public static TheoryData<string> EveryCode()
    {
        var codes = new TheoryData<string>();
        foreach (var code in Constants(typeof(IpcCodes)).Values)
        {
            codes.Add(code);
        }

        return codes;
    }

    [Fact]
    public void TheWalkFindsTheCodesItIsMeantToFind()
    {
        var all = Constants(typeof(IpcCodes));

        Assert.True(all.Count >= 30, $"only {all.Count} codes found");
        Assert.Contains(IpcCodes.ConfigCoolDownClamped, all.Values);
        Assert.Contains(IpcCodes.RequestInvalidJson, all.Values);
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void EveryCodeIsWrittenAreaDotReason(string code)
    {
        Assert.Matches(new Regex("^[a-z]+\\.[a-z0-9]+(-[a-z0-9]+)*$"), code);
    }

    [Fact]
    public void NoTwoConstantsShareACode()
    {
        var values = Constants(typeof(IpcCodes)).Values.ToList();

        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The engine cannot reference the wire, so it keeps its own list. Same name, same value, or the
    /// interface would be handed a code nobody translated.
    /// </summary>
    [Fact]
    public void EveryCodeTheEngineSendsIsOnTheWiresList()
    {
        var wire = Constants(typeof(IpcCodes));
        var engine = Constants(typeof(SessionCodes));

        Assert.NotEmpty(engine);
        foreach (var (name, value) in engine)
        {
            Assert.True(wire.TryGetValue(name, out var onTheWire), $"IpcCodes has no {name}.");
            Assert.Equal(value, onTheWire);
        }
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void EveryCodeHasEnglishTextInTheCli(string code)
    {
        var text = ServiceText.English(code, ["30"]);

        Assert.False(string.IsNullOrWhiteSpace(text), $"{code} has no text.");
        Assert.NotEqual(code, text);
    }

    /// <summary>A newer service's code is printed as it came.</summary>
    [Fact]
    public void TheCliPrintsACodeItDoesNotKnowAsItself()
    {
        Assert.Equal("dns.port-53-taken", ServiceText.English("dns.port-53-taken", ["1"]));
    }

    [Fact]
    public void TheCliPutsTheArgumentsIntoTheSentence()
    {
        Assert.Equal(
            "Cool-down was clamped to 30 minutes.",
            ServiceText.English(IpcCodes.ConfigCoolDownClamped, ["30"]));
    }

    /// <summary>A notice from a service that sent fewer values than this build expects is quoted, not thrown.</summary>
    [Fact]
    public void TheCliPrintsTheCodeWhenTheArgumentsAreMissing()
    {
        Assert.Equal(IpcCodes.ConfigCoolDownClamped, ServiceText.English(IpcCodes.ConfigCoolDownClamped, null));
        Assert.Equal(IpcCodes.ConfigCoolDownClamped, ServiceText.English(IpcCodes.ConfigCoolDownClamped, []));
    }

    [Fact]
    public void TheCliReadsTheSameAsBeforeWhereTheSentenceHadNoValues()
    {
        Assert.Equal(
            "The rule was removed from the configuration. The session already running keeps it until it ends.",
            ServiceText.English(IpcCodes.RulesRemovalWaitsForNextSession));
        Assert.Equal(
            "The configuration has no site rule for that domain.",
            ServiceText.English(IpcCodes.RulesSiteNotFound));
    }

    /// <summary>The wire's values, written out: a renamed constant keeps other tests green while a peer of the same version stops recognising it.</summary>
    [Fact]
    public void TheWireValuesAreTheOnesThisVersionShipped()
    {
        var shipped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RequestInvalidJson"] = "request.invalid-json",
            ["RequestEmpty"] = "request.empty",
            ["CommandUnknown"] = "command.unknown",
            ["SessionStartWrongState"] = "session.start-wrong-state",
            ["SessionEmptyBlockList"] = "session.empty-block-list",
            ["SessionExtendWrongState"] = "session.extend-wrong-state",
            ["SessionCannotShorten"] = "session.cannot-shorten",
            ["SessionExtendNeedsMinutes"] = "session.extend-needs-minutes",
            ["SessionNoneToExtend"] = "session.none-to-extend",
            ["SessionDurationClamped"] = "session.duration-clamped",
            ["SessionCoolDownClamped"] = "session.cooldown-clamped",
            ["SessionEndClamped"] = "session.end-clamped",
            ["SessionProtectedAppSkipped"] = "session.protected-app-skipped",
            ["UnlockRequestWrongState"] = "unlock.request-wrong-state",
            ["UnlockCancelWrongState"] = "unlock.cancel-wrong-state",
            ["RulesAddWrongState"] = "rules.add-wrong-state",
            ["RulesFrozen"] = "rules.frozen",
            ["RulesSiteMissing"] = "rules.site-missing",
            ["RulesSiteDomainBlank"] = "rules.site-domain-blank",
            ["RulesSiteNotFound"] = "rules.site-not-found",
            ["RulesAppMissing"] = "rules.app-missing",
            ["RulesAppKindUnknown"] = "rules.app-kind-unknown",
            ["RulesAppValueBlank"] = "rules.app-value-blank",
            ["RulesAppNotFound"] = "rules.app-not-found",
            ["RulesAppProtected"] = "rules.app-protected",
            ["RulesAppProtectedSystemFile"] = "rules.app-protected-system-file",
            ["RulesAppProtectedSystemDirectory"] = "rules.app-protected-system-directory",
            ["RulesRemovalWaitsForNextSession"] = "rules.removal-waits-for-next-session",
            ["ConfigSettingsMissing"] = "config.settings-missing",
            ["ConfigLanguageBlank"] = "config.language-blank",
            ["ConfigCoolDownClamped"] = "config.cooldown-clamped",
            ["ConfigSessionDurationClamped"] = "config.session-duration-clamped",
            ["ConfigCoolDownAppliesNextSession"] = "config.cooldown-applies-next-session",
        };

        Assert.Equal(
            shipped.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            Constants(typeof(IpcCodes)).OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    /// <summary>The sentence each code prints, written out: a code mapped to another code's sentence gives the wrong reason.</summary>
    public static TheoryData<string, string[], string> CliSentences() => new()
    {
        { IpcCodes.RequestInvalidJson, [], "Request is not valid JSON." },
        { IpcCodes.RequestEmpty, [], "Request is empty." },
        { IpcCodes.CommandUnknown, [], "The service does not know that command." },
        { IpcCodes.SessionStartWrongState, [], "Cannot start a session while one is running or its block is still being lifted." },
        { IpcCodes.SessionEmptyBlockList, [], "Cannot start a session with an empty block list." },
        { IpcCodes.SessionExtendWrongState, [], "Cannot change the end time: no session is running." },
        { IpcCodes.SessionCannotShorten, [], "A session can only be extended, never shortened." },
        { IpcCodes.SessionExtendNeedsMinutes, [], "ExtendSession requires a positive number of minutes." },
        { IpcCodes.SessionNoneToExtend, [], "There is no session to extend." },
        { IpcCodes.SessionDurationClamped, ["30"], "Session duration was clamped to 30 minutes." },
        { IpcCodes.SessionCoolDownClamped, ["30"], "Cool-down was clamped to 30 minutes." },
        { IpcCodes.SessionEndClamped, ["30"], "End time was clamped to the maximum session duration of 30 minutes." },
        { IpcCodes.SessionProtectedAppSkipped, ["game.exe"], "'game.exe' is protected and was skipped." },
        { IpcCodes.UnlockRequestWrongState, [], "Cannot request unlock: no session is running, or an unlock is already pending." },
        { IpcCodes.UnlockCancelWrongState, [], "Cannot cancel unlock: no unlock is pending." },
        { IpcCodes.RulesAddWrongState, [], "Cannot add a rule: no session is running." },
        { IpcCodes.RulesFrozen, [], "Cannot change the rules while a finished session is still being cleared." },
        { IpcCodes.RulesSiteMissing, [], "This command requires a site rule." },
        { IpcCodes.RulesSiteDomainBlank, [], "A site rule needs a domain." },
        { IpcCodes.RulesSiteNotFound, [], "The configuration has no site rule for that domain." },
        { IpcCodes.RulesAppMissing, [], "This command requires an app rule." },
        { IpcCodes.RulesAppKindUnknown, [], "That is not a match kind this service knows." },
        { IpcCodes.RulesAppValueBlank, [], "An app rule needs a value." },
        { IpcCodes.RulesAppNotFound, [], "The configuration has no app rule for that application." },
        { IpcCodes.RulesAppProtected, [], "That application is protected and cannot be blocked." },
        { IpcCodes.RulesAppProtectedSystemFile, [], "That application is required by Windows or by Chronos itself." },
        { IpcCodes.RulesAppProtectedSystemDirectory, [], "That application lives in a Windows system directory." },
        {
            IpcCodes.RulesRemovalWaitsForNextSession,
            [],
            "The rule was removed from the configuration. The session already running keeps it until it ends."
        },
        { IpcCodes.ConfigSettingsMissing, [], "UpdateSettings requires a settings block." },
        { IpcCodes.ConfigLanguageBlank, [], "Language cannot be blank." },
        { IpcCodes.ConfigCoolDownClamped, ["30"], "Cool-down was clamped to 30 minutes." },
        { IpcCodes.ConfigSessionDurationClamped, ["30"], "Default session duration was clamped to 30 minutes." },
        { IpcCodes.ConfigCoolDownAppliesNextSession, [], "The session already running keeps the cool-down it began with." },
    };

    [Theory]
    [MemberData(nameof(CliSentences))]
    public void EachCodePrintsItsOwnSentence(string code, string[] arguments, string sentence)
    {
        Assert.Equal(sentence, ServiceText.English(code, arguments));
    }

    [Fact]
    public void EveryCodeHasARowInTheSentenceTable()
    {
        var rows = CliSentences().Select(row => (string)row[0]).ToList();

        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Constants(typeof(IpcCodes)).Values.Order(StringComparer.Ordinal),
            rows.Order(StringComparer.Ordinal));
    }

    /// <summary>The codes that carry a value: the value is in the sentence, not dropped by it.</summary>
    [Theory]
    [InlineData(IpcCodes.SessionDurationClamped)]
    [InlineData(IpcCodes.SessionCoolDownClamped)]
    [InlineData(IpcCodes.SessionEndClamped)]
    [InlineData(IpcCodes.SessionProtectedAppSkipped)]
    [InlineData(IpcCodes.ConfigCoolDownClamped)]
    [InlineData(IpcCodes.ConfigSessionDurationClamped)]
    public void AValueACodeCarriesReachesTheCliSentence(string code)
    {
        Assert.Contains("⟦v⟧", ServiceText.English(code, ["⟦v⟧"]), StringComparison.Ordinal);
    }

    /// <summary>"1 minutes" is what is printed when the minimum cool-down is clamped to.</summary>
    [Theory]
    [InlineData(IpcCodes.SessionDurationClamped, "Session duration was clamped to 1 minute.")]
    [InlineData(IpcCodes.SessionCoolDownClamped, "Cool-down was clamped to 1 minute.")]
    [InlineData(IpcCodes.SessionEndClamped, "End time was clamped to the maximum session duration of 1 minute.")]
    [InlineData(IpcCodes.ConfigCoolDownClamped, "Cool-down was clamped to 1 minute.")]
    [InlineData(IpcCodes.ConfigSessionDurationClamped, "Default session duration was clamped to 1 minute.")]
    public void OneMinuteIsSingular(string code, string sentence)
    {
        Assert.Equal(sentence, ServiceText.English(code, ["1"]));
    }

    /// <summary>A malformed or foreign answer can carry a notice with no code, or none where one should be; the terminal says so.</summary>
    [Fact]
    public void ANoticeWithNoCodeIsPrintedAsUnknownRatherThanThrown()
    {
        var output = new StringWriter();

        Cli.Program.Print(
            IpcResponse.Ok(Idle(), [new IpcNotice(null!, null!), null!]),
            output,
            TextWriter.Null);

        var warnings = output.ToString().Split(Environment.NewLine).Where(line => line.StartsWith("warning:", StringComparison.Ordinal));
        Assert.Equal(["warning: (no code)", "warning: (no code)"], warnings);
    }

    [Fact]
    public void TheCliPrintsAWarningInWords()
    {
        var output = new StringWriter();

        Cli.Program.Print(
            IpcResponse.Ok(Idle(), [new IpcNotice(IpcCodes.ConfigCoolDownClamped, ["60"])]),
            output,
            TextWriter.Null);

        Assert.Contains("warning: Cool-down was clamped to 60 minutes.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCliPrintsARefusalInWords()
    {
        var error = new StringWriter();

        Cli.Program.Print(IpcResponse.Fail(IpcCodes.SessionNoneToExtend), TextWriter.Null, error);

        Assert.Equal("There is no session to extend.", error.ToString().Trim());
    }

    /// <summary>The version refusal, and anything else that is not a code, reaches the terminal as it came.</summary>
    [Fact]
    public void TheCliPrintsARefusalItCannotWordAsItCame()
    {
        var error = new StringWriter();

        Cli.Program.Print(IpcResponse.Fail("Unsupported protocol version 5; this service speaks version 4."), TextWriter.Null, error);

        Assert.Equal("Unsupported protocol version 5; this service speaks version 4.", error.ToString().Trim());
    }

    private static StatusPayload Idle() => new("Idle", Moment, null, null, null, 5, [], [], []);

    private static Dictionary<string, string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);
}
