using System.Text.Json;
using Chronos.Ipc;

namespace Chronos.Service.Tests;

/// <summary>The wire itself: its version and the event shapes.</summary>
public sealed class IpcProtocolTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheProtocolIsAtVersionFour()
    {
        // A literal on purpose: Error and Warnings became codes in 4, so older and newer sides must refuse each other.
        Assert.Equal(4, IpcProtocol.Version);
    }

    [Fact]
    public void AWarningCrossesTheWireAsItsCodeAndArguments()
    {
        var line = JsonSerializer.Serialize(
            IpcResponse.Ok(Status(), [new IpcNotice(IpcCodes.ConfigCoolDownClamped, ["30"])]),
            IpcJson.Options);

        var notice = Assert.Single(JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)!.Warnings);

        Assert.Equal(IpcCodes.ConfigCoolDownClamped, notice.Code);
        Assert.Equal(["30"], notice.Arguments);
    }

    [Fact]
    public void ASiteBlockedEventCarriesTheDomainAndTheStatusItWasRefusedIn()
    {
        var status = Status();

        var published = IpcEvent.SiteBlocked(status, "reddit.com");

        Assert.Equal(IpcEventKind.SiteBlocked, published.Kind);
        Assert.Equal("reddit.com", published.Domain);
        Assert.Same(status, published.Status);
        Assert.Equal(status.Now, published.At);
        Assert.Null(published.AppName);
    }

    [Fact]
    public void AnAppBlockedEventCarriesNoDomain()
    {
        Assert.Null(IpcEvent.AppBlocked(Status(), "steam.exe").Domain);
    }

    [Fact]
    public void TheKindOfASiteBlockIsItsOwnWord()
    {
        Assert.Equal("SiteBlocked", IpcEventKind.SiteBlocked);
    }

    [Fact]
    public void ASiteBlockedEventCrossesTheWireWithItsDomain()
    {
        var line = JsonSerializer.Serialize(IpcEvent.SiteBlocked(Status(), "reddit.com"), IpcJson.Options);

        var read = JsonSerializer.Deserialize<IpcEvent>(line, IpcJson.Options)!;

        Assert.Equal(IpcEventKind.SiteBlocked, read.Kind);
        Assert.Equal("reddit.com", read.Domain);
        Assert.Equal(Moment + TimeSpan.FromMinutes(42), read.Status!.EndsAt);
    }

    private static StatusPayload Status() =>
        new("Active", Moment, Moment, Moment + TimeSpan.FromMinutes(42), null, 30, [], [], []);
}
