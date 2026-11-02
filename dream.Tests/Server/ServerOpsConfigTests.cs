using System.Net;
using Xunit;
using Dream.Server;

namespace Dream.Tests.Server;

// M8. The dedicated server's environment settings, parsed without binding anything: a pure function over
// a variable source. Nothing here opens a socket.
public sealed class ServerOpsConfigTests
{
    private static ServerRuntimeConfig? Parse(Dictionary<string, string> env, List<string>? log = null) =>
        ServerRuntimeConfig.Parse(name => env.GetValueOrDefault(name), log == null ? null : log.Add);

    [Fact]
    public void Defaults_KeepTheExistingBehaviour()
    {
        ServerRuntimeConfig config = Parse([])!;

        Assert.Equal(IPAddress.Any, config.BindAddress);
        Assert.Equal(ServerBootstrap.DefaultPort, config.Port);
        Assert.Equal(64, config.MaxPlayers);
        Assert.Null(config.Motd);
    }

    [Fact]
    public void ValidValues_AreTaken()
    {
        ServerRuntimeConfig config = Parse(new()
        {
            [ServerRuntimeConfig.PortEnvironmentVariable] = " 6001 ",
            [ServerRuntimeConfig.BindAddressEnvironmentVariable] = "127.0.0.1",
            [ServerRuntimeConfig.MaxPlayersEnvironmentVariable] = "12",
            [ServerRuntimeConfig.MotdEnvironmentVariable] = "  Welcome, traveller  ",
        })!;

        Assert.Equal(6001, config.Port);
        Assert.Equal(IPAddress.Loopback, config.BindAddress);
        Assert.Equal(12, config.MaxPlayers);
        Assert.Equal("Welcome, traveller", config.Motd);
    }

    [Fact]
    public void AnIpv6Address_IsAccepted()
    {
        ServerRuntimeConfig config = Parse(new() { [ServerRuntimeConfig.BindAddressEnvironmentVariable] = "::1" })!;

        Assert.Equal(IPAddress.IPv6Loopback, config.BindAddress);
        Assert.Equal("[::1]:5057", ServerBootstrap.FormatEndpoint(config.BindAddress, 5057));
        Assert.Equal("127.0.0.1:5057", ServerBootstrap.FormatEndpoint(IPAddress.Loopback, 5057));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("80x")]
    [InlineData("1.5")]
    public void AnInvalidPort_StopsTheStart(string value)
    {
        List<string> log = [];

        ServerRuntimeConfig? config = Parse(new() { [ServerRuntimeConfig.PortEnvironmentVariable] = value }, log);

        Assert.Null(config);
        Assert.Contains(log, line => line.Contains(ServerRuntimeConfig.PortEnvironmentVariable, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    public void PortBounds_AreInclusive(string value)
    {
        Assert.NotNull(Parse(new() { [ServerRuntimeConfig.PortEnvironmentVariable] = value }));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("not-an-address")]
    [InlineData("300.1.1.1")]
    public void AnInvalidAddress_StopsTheStart(string value)
    {
        List<string> log = [];

        ServerRuntimeConfig? config = Parse(new() { [ServerRuntimeConfig.BindAddressEnvironmentVariable] = value }, log);

        Assert.Null(config);
        Assert.Contains(log, line => line.Contains(ServerRuntimeConfig.BindAddressEnvironmentVariable, StringComparison.Ordinal));
    }

    [Fact]
    public void ABlankValue_IsTheSameAsUnset()
    {
        ServerRuntimeConfig config = Parse(new()
        {
            [ServerRuntimeConfig.PortEnvironmentVariable] = "  ",
            [ServerRuntimeConfig.BindAddressEnvironmentVariable] = "",
            [ServerRuntimeConfig.MaxPlayersEnvironmentVariable] = " ",
        })!;

        Assert.Equal(ServerBootstrap.DefaultPort, config.Port);
        Assert.Equal(IPAddress.Any, config.BindAddress);
        Assert.Equal(64, config.MaxPlayers);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public void AnInvalidPlayerCap_KeepsTheDefault_WithALogLine(string value)
    {
        List<string> log = [];

        ServerRuntimeConfig config = Parse(new() { [ServerRuntimeConfig.MaxPlayersEnvironmentVariable] = value }, log)!;

        Assert.Equal(64, config.MaxPlayers);
        Assert.Single(log);
    }

    [Fact]
    public void Motd_IsCleaned_AndCut()
    {
        Assert.Null(ServerRuntimeConfig.SanitizeMotd(null));
        Assert.Null(ServerRuntimeConfig.SanitizeMotd("   "));
        Assert.Null(ServerRuntimeConfig.SanitizeMotd("\u0007\u202E"));
        Assert.Equal("Hello", ServerRuntimeConfig.SanitizeMotd("He\u0000l\u202Elo"));

        string cut = ServerRuntimeConfig.SanitizeMotd(new string('m', 1000))!;
        Assert.Equal(ServerRuntimeConfig.MaxMotdLength, cut.Length);
    }

    [Fact]
    public void Motd_NeverEndsInAHalfSurrogate()
    {
        string text = new string('a', ServerRuntimeConfig.MaxMotdLength - 1) + "\U0001F600" + "tail";

        string cut = ServerRuntimeConfig.SanitizeMotd(text)!;

        Assert.False(char.IsHighSurrogate(cut[^1]));
        Assert.True(cut.Length <= ServerRuntimeConfig.MaxMotdLength);
    }

    [Fact]
    public void TheServerNeverReadsTheClientOnlyPortVariable()
    {
        List<string> asked = [];

        ServerRuntimeConfig.Parse(name => { asked.Add(name); return null; }, null);

        Assert.DoesNotContain("DREAM_SERVER_PORT", asked);
        Assert.Contains(ServerRuntimeConfig.PortEnvironmentVariable, asked);
    }

    [Fact]
    public void TheInvalidConfigExitCode_IsNine_AndDistinct()
    {
        Assert.Equal(9, ServerBootstrap.InvalidConfigExitCode);
        int[] others =
        [
            ServerBootstrap.SchemaTooNewExitCode, ServerBootstrap.WorldNotFoundExitCode, ServerBootstrap.WorldCannotBeOpenedExitCode,
            ServerBootstrap.DatabaseIntegrityExitCode, ServerBootstrap.WorldHeldExitCode, ServerBootstrap.ContentInvalidExitCode,
        ];
        Assert.DoesNotContain(ServerBootstrap.InvalidConfigExitCode, others);
    }
}
