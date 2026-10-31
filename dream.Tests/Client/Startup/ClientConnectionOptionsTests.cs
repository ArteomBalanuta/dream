using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;

namespace Dream.Tests.Client.Startup;


public class ClientConnectionOptionsTests
{
    [Fact]
    public void TryCreateRemoteOptions_Rejects_Blank_Host()
    {
        ClientConnectionOptions options = new("", "5057", "player-a");

        bool valid = options.TryCreateRemoteOptions(out RemoteClientOptions? resolved, out string? error);

        Assert.False(valid);
        Assert.Null(resolved);
        Assert.Equal("Host is required.", error);
    }

    [Fact]
    public void TryCreateRemoteOptions_Rejects_Invalid_Port()
    {
        ClientConnectionOptions options = new("127.0.0.1", "abc", "player-a");

        bool valid = options.TryCreateRemoteOptions(out RemoteClientOptions? resolved, out string? error);

        Assert.False(valid);
        Assert.Null(resolved);
        Assert.Equal("Port must be a valid integer between 1 and 65535.", error);
    }

    [Fact]
    public void TryCreateRemoteOptions_Builds_Remote_Options_For_Valid_Input()
    {
        ClientConnectionOptions options = new("127.0.0.1", "5057", "player-a");

        bool valid = options.TryCreateRemoteOptions(out RemoteClientOptions? resolved, out string? error);

        Assert.True(valid);
        Assert.NotNull(resolved);
        Assert.Null(error);
        Assert.Equal("127.0.0.1", resolved!.Host);
        Assert.Equal(5057, resolved.Port);
        Assert.Equal("player-a", resolved.DisplayName);
        Assert.Equal("player-a", resolved.ClientName);
        Assert.StartsWith("client-", resolved.SessionId);
    }

    [Fact]
    public void TryCreateRemoteOptions_CarriesTheAccountToken_AndNeverPrintsIt()
    {
        ClientConnectionOptions options = new("127.0.0.1", "5057", "player-a");

        options.TryCreateRemoteOptions(out RemoteClientOptions? resolved, out _, accountToken: "secret-token-value");

        Assert.Equal("secret-token-value", resolved!.AccountToken);
        Assert.DoesNotContain("secret-token-value", resolved.ToString());
    }
}
