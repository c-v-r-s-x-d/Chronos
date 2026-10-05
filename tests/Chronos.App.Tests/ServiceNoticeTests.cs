using System.Globalization;
using System.Reflection;
using Chronos.App.Localization;
using Chronos.App.Resources;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The service refuses and warns in codes; every word the user reads is this interface's, in the screen's language.</summary>
[Collection(CultureBound.Name)]
public sealed class ServiceNoticeTests
{
    // Values no translation would contain, so a placeholder a language forgot shows up as missing.
    private static readonly string[] Markers = ["⟦a⟧", "⟦b⟧", "⟦c⟧"];

    public static TheoryData<string> EveryCode()
    {
        var codes = new TheoryData<string>();
        foreach (var code in Codes())
        {
            codes.Add(code);
        }

        return codes;
    }

    [Fact]
    public void TheCodesAreReadFromTheProtocolAndNotFromNothing()
    {
        var all = Codes();

        Assert.True(all.Count >= 30, $"only {all.Count} codes found");
        Assert.Contains(IpcCodes.ConfigCoolDownClamped, all);
        Assert.Contains(IpcCodes.RulesRemovalWaitsForNextSession, all);
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void EveryCodeHasEnglishText(string code)
    {
        using var english = new UiCulture("en-US");

        var text = ServiceNotice.Text(code, Markers);

        Assert.False(string.IsNullOrWhiteSpace(text), $"{code} has no text.");
        Assert.NotEqual(code, text);
    }

    /// <summary>Russian of its own, not the English fallback, and with the same values in it as the English.</summary>
    [Theory]
    [MemberData(nameof(EveryCode))]
    public void EveryCodeHasRussianTextWithTheSameValuesAsTheEnglish(string code)
    {
        string english;
        using (var _ = new UiCulture("en-US"))
        {
            english = ServiceNotice.Text(code, Markers);
        }

        using var russian = new UiCulture("ru-RU");
        var text = ServiceNotice.Text(code, Markers);

        Assert.NotEqual(code, text);
        Assert.NotEqual(english, text);
        Assert.Equal(
            Markers.Where(marker => english.Contains(marker, StringComparison.Ordinal)),
            Markers.Where(marker => text.Contains(marker, StringComparison.Ordinal)));
    }

    [Fact]
    public void AWarningPutsItsValueIntoTheRussianSentence()
    {
        using var russian = new UiCulture("ru-RU");

        var text = ServiceNotice.Text(IpcCodes.ConfigCoolDownClamped, ["30"]);

        Assert.Contains("30", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
    }

    /// <summary>A newer service's code is shown as itself, never "unknown error"; the user can quote it.</summary>
    [Fact]
    public void AnUnknownCodeIsShownAsItself()
    {
        Assert.Equal("rules.too-many", ServiceNotice.Text("rules.too-many", ["5"]));
    }

    [Fact]
    public void AKnownCodeWithTooFewValuesIsShownAsItselfRatherThanThrowing()
    {
        using var english = new UiCulture("en-US");

        Assert.Equal(IpcCodes.ConfigCoolDownClamped, ServiceNotice.Text(IpcCodes.ConfigCoolDownClamped, null));
        Assert.Equal(IpcCodes.ConfigCoolDownClamped, ServiceNotice.Text(IpcCodes.ConfigCoolDownClamped, []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoCodeIsNoText(string? code)
    {
        Assert.Equal(string.Empty, ServiceNotice.Text(code));
    }

    /// <summary>Each code's sentence in both languages, written out, so swapped or mismapped sentences are caught.</summary>
    public static TheoryData<string, string, string> Sentences() => new()
    {
        { IpcCodes.RequestInvalidJson, "The service could not read the request.", "Служба не смогла прочитать запрос." },
        { IpcCodes.RequestEmpty, "The request to the service was empty.", "Запрос к службе оказался пустым." },
        {
            IpcCodes.CommandUnknown,
            "The service does not know this command. It may be older than this window.",
            "Служба не знает этой команды — возможно, версия службы старше этого окна."
        },
        {
            IpcCodes.SessionStartWrongState,
            "A session is already running, or the block of the last one is still being lifted.",
            "Сессия уже идёт, или блокировка прошлой сессии ещё снимается."
        },
        {
            IpcCodes.SessionEmptyBlockList,
            "There is nothing to block. Add a site or an application first.",
            "Блокировать нечего. Сначала добавьте сайт или приложение."
        },
        { IpcCodes.SessionExtendWrongState, "There is no running session to extend.", "Нет идущей сессии, которую можно продлить." },
        { IpcCodes.SessionCannotShorten, "A session can only be extended, never shortened.", "Сессию можно только продлить, но не сократить." },
        {
            IpcCodes.SessionExtendNeedsMinutes,
            "A session can be extended only by a positive number of minutes.",
            "Продлить сессию можно только на положительное число минут."
        },
        { IpcCodes.SessionNoneToExtend, "There is no session to extend.", "Нет сессии, которую можно продлить." },
        {
            IpcCodes.SessionDurationClamped,
            "The session length was set to ⟦v⟧ min, the nearest allowed value.",
            "Длительность сессии установлена на ⟦v⟧ мин. — ближайшее допустимое значение."
        },
        {
            IpcCodes.SessionCoolDownClamped,
            "The delay before the block lifts was set to ⟦v⟧ min, the nearest allowed value.",
            "Задержка снятия блокировки установлена на ⟦v⟧ мин. — ближайшее допустимое значение."
        },
        {
            IpcCodes.SessionEndClamped,
            "A session cannot last longer than ⟦v⟧ min, so it now ends at that limit.",
            "Сессия не может длиться дольше ⟦v⟧ мин., поэтому теперь она закончится на этом пределе."
        },
        {
            IpcCodes.SessionProtectedAppSkipped,
            "⟦v⟧ is protected and was left out of the session.",
            "Приложение ⟦v⟧ защищено и не вошло в сессию."
        },
        {
            IpcCodes.UnlockRequestWrongState,
            "Lifting the block can be requested only during a session, and only if it has not been requested already.",
            "Попросить снять блокировку можно только во время сессии, если запрос ещё не отправлен."
        },
        {
            IpcCodes.UnlockCancelWrongState,
            "There is nothing to cancel: no request to lift the block is pending.",
            "Отменять нечего: запроса на снятие блокировки нет."
        },
        {
            IpcCodes.RulesAddWrongState,
            "Rules can be added to a session only while it is running.",
            "Добавить правило в сессию можно, только пока она идёт."
        },
        {
            IpcCodes.RulesFrozen,
            "The session has just ended and the block is still being lifted. Try again in a moment.",
            "Сессия только что закончилась, и блокировка ещё снимается. Попробуйте чуть позже."
        },
        { IpcCodes.RulesSiteMissing, "The request named no site.", "В запросе не указан сайт." },
        { IpcCodes.RulesSiteDomainBlank, "Enter a site address.", "Введите адрес сайта." },
        { IpcCodes.RulesSiteNotFound, "There is no rule for that site in the list.", "Правила для этого сайта в списке нет." },
        { IpcCodes.RulesAppMissing, "The request named no application.", "В запросе не указано приложение." },
        {
            IpcCodes.RulesAppKindUnknown,
            "The service does not know this kind of application rule.",
            "Служба не знает такого вида правила для приложения."
        },
        { IpcCodes.RulesAppValueBlank, "Choose an application.", "Выберите приложение." },
        { IpcCodes.RulesAppNotFound, "There is no rule for that application in the list.", "Правила для этого приложения в списке нет." },
        {
            IpcCodes.RulesAppProtected,
            "This application is protected and cannot be blocked.",
            "Это приложение защищено, его нельзя заблокировать."
        },
        {
            IpcCodes.RulesAppProtectedSystemFile,
            "Windows or Chronos itself needs this application, so it cannot be blocked.",
            "Это приложение нужно Windows или самому Chronos, поэтому его нельзя заблокировать."
        },
        {
            IpcCodes.RulesAppProtectedSystemDirectory,
            "This application lives in a Windows system folder, so it cannot be blocked.",
            "Это приложение находится в системной папке Windows, поэтому его нельзя заблокировать."
        },
        {
            IpcCodes.RulesRemovalWaitsForNextSession,
            "The rule was removed from the list. The session already running keeps it until it ends.",
            "Правило удалено из списка. Идущая сессия сохранит его до своего окончания."
        },
        {
            IpcCodes.ConfigSettingsMissing,
            "The request to change the settings carried no settings.",
            "В запросе на изменение настроек нет самих настроек."
        },
        { IpcCodes.ConfigLanguageBlank, "Choose a language.", "Выберите язык." },
        {
            IpcCodes.ConfigCoolDownClamped,
            "The delay before the block lifts was set to ⟦v⟧ min, the nearest allowed value.",
            "Задержка снятия блокировки установлена на ⟦v⟧ мин. — ближайшее допустимое значение."
        },
        {
            IpcCodes.ConfigSessionDurationClamped,
            "The default session length was set to ⟦v⟧ min, the nearest allowed value.",
            "Длительность сессии по умолчанию установлена на ⟦v⟧ мин. — ближайшее допустимое значение."
        },
        {
            IpcCodes.ConfigCoolDownAppliesNextSession,
            "The session already running keeps the delay it started with; the new one applies from the next session.",
            "Идущая сессия сохраняет прежнюю задержку снятия; новая действует со следующей сессии."
        },
    };

    [Theory]
    [MemberData(nameof(Sentences))]
    public void EachCodeReadsAsItsOwnSentenceInBothLanguages(string code, string english, string russian)
    {
        using (var _ = new UiCulture("en-US"))
        {
            Assert.Equal(english, ServiceNotice.Text(code, ["⟦v⟧"]));
        }

        using var ru = new UiCulture("ru-RU");

        Assert.Equal(russian, ServiceNotice.Text(code, ["⟦v⟧"]));
    }

    [Fact]
    public void EveryCodeHasARowInTheSentenceTable()
    {
        var rows = Sentences().Select(row => (string)row[0]).ToList();

        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Codes().Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));
    }

    /// <summary>Each code reads from the resource named Notice + its constant; two codes can share a sentence, so only the key tells them apart.</summary>
    [Theory]
    [MemberData(nameof(EveryConstant))]
    public void EachCodeReadsFromTheResourceNamedAfterIt(string name, string code)
    {
        foreach (var language in new[] { "en-US", "ru-RU" })
        {
            using var culture = new UiCulture(language);

            var template = Strings.ResourceManager.GetString("Notice" + name, CultureInfo.CurrentUICulture);

            Assert.True(template is not null, $"There is no resource Notice{name}.");
            Assert.Equal(
                string.Format(CultureInfo.CurrentUICulture, template!, Markers),
                ServiceNotice.Text(code, Markers));
        }
    }

    public static TheoryData<string, string> EveryConstant()
    {
        var constants = new TheoryData<string, string>();
        foreach (var field in Fields())
        {
            constants.Add(field.Name, (string)field.GetRawConstantValue()!);
        }

        return constants;
    }

    [Theory]
    [InlineData(IpcCodes.SessionDurationClamped)]
    [InlineData(IpcCodes.SessionCoolDownClamped)]
    [InlineData(IpcCodes.SessionEndClamped)]
    [InlineData(IpcCodes.SessionProtectedAppSkipped)]
    [InlineData(IpcCodes.ConfigCoolDownClamped)]
    [InlineData(IpcCodes.ConfigSessionDurationClamped)]
    public void AValueACodeCarriesReachesTheSentenceInBothLanguages(string code)
    {
        foreach (var language in new[] { "en-US", "ru-RU" })
        {
            using var culture = new UiCulture(language);

            Assert.Contains(Markers[0], ServiceNotice.Text(code, Markers), StringComparison.Ordinal);
        }
    }

    private static IEnumerable<FieldInfo> Fields() =>
        typeof(IpcCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string));

    private static List<string> Codes() =>
    [
        .. typeof(IpcCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
    ];
}
