using Xunit;
using Dream.Server;

namespace Dream.Tests.Server;

// M8. The admin command line parser: pure, no host, no role. Who may run a command is the host's decision
// (M8EndToEndLoopbackTests); here only the grammar.
public sealed class AdminCommandsTests
{
    private static AdminCommand Parse(string line)
    {
        Assert.True(AdminCommands.TryParse(line, out AdminCommand command, out string error), error);
        return command;
    }

    private static string Refused(string line)
    {
        Assert.False(AdminCommands.TryParse(line, out _, out string error));
        Assert.NotEmpty(error);
        return error;
    }

    [Theory]
    [InlineData("kick Bob")]
    [InlineData("/kick Bob")]
    [InlineData("  /KICK   bob ")]
    public void Kick_WithOrWithoutSlash_AnyCase(string line)
    {
        AdminCommand command = Parse(line);

        Assert.Equal(AdminCommandKind.Kick, command.Kind);
        Assert.Equal("Bob", command.Target, ignoreCase: true);
        Assert.Equal(string.Empty, command.Text);
    }

    [Fact]
    public void Kick_TakesAReason()
    {
        AdminCommand command = Parse("/kick Bob stop spamming");

        Assert.Equal("stop spamming", command.Text);
        Assert.Null(command.Minutes);
    }

    [Fact]
    public void Ban_MinutesAreOptional_AndTheRestIsTheReason()
    {
        AdminCommand permanent = Parse("/ban Bob cheating with a long reason");
        AdminCommand timed = Parse("/ban Bob 90 cheating");
        AdminCommand timedNoReason = Parse("/ban Bob 5");

        Assert.Null(permanent.Minutes);
        Assert.Equal("cheating with a long reason", permanent.Text);
        Assert.Equal(90, timed.Minutes);
        Assert.Equal("cheating", timed.Text);
        Assert.Equal(5, timedNoReason.Minutes);
        Assert.Equal(string.Empty, timedNoReason.Text);
    }

    [Fact]
    public void QuotedNames_MayHaveSpaces()
    {
        AdminCommand ban = Parse("/ban \"Name with spaces\" 10 griefing the castle");
        AdminCommand mute = Parse("mute \"Two Words\"");

        Assert.Equal("Name with spaces", ban.Target);
        Assert.Equal(10, ban.Minutes);
        Assert.Equal("griefing the castle", ban.Text);
        Assert.Equal("Two Words", mute.Target);
        Assert.Null(mute.Minutes);
    }

    [Fact]
    public void Unban_Unmute_TakeOnlyAName()
    {
        Assert.Equal(AdminCommandKind.Unban, Parse("/unban Bob").Kind);
        Assert.Equal(AdminCommandKind.Unmute, Parse("/unmute Bob").Kind);
        Refused("/unban Bob extra");
        Refused("/unmute Bob 5");
    }

    [Fact]
    public void Mute_TakesMinutes_ButNoReason()
    {
        Assert.Equal(30, Parse("/mute Bob 30").Minutes);
        Refused("/mute Bob 30 because");
        Refused("/mute Bob because");
    }

    [Fact]
    public void Announce_KeepsTheWholeLine()
    {
        AdminCommand command = Parse("/announce The server restarts in 5 minutes. \"Soon\".");

        Assert.Equal(AdminCommandKind.Announce, command.Kind);
        Assert.Equal("The server restarts in 5 minutes. \"Soon\".", command.Text);
        Assert.Equal(string.Empty, command.Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("/frobnicate Bob")]
    [InlineData("/kick")]
    [InlineData("/ban")]
    [InlineData("/announce")]
    [InlineData("/ban \"unclosed 5")]
    public void Malformed_IsRefused_WithUsageText(string line)
    {
        Refused(line);
    }

    [Theory]
    [InlineData("/ban Bob 0")]
    [InlineData("/ban Bob 99999999")]
    [InlineData("/mute Bob 5256001")]
    public void Minutes_OutsideTheRange_AreRefused(string line)
    {
        Assert.Contains("Minutes", Refused(line), StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_OnNamesReasonsAndAnnouncements()
    {
        Refused("/kick " + new string('n', AdminCommands.MaxTargetLength + 1));
        Refused("/ban Bob 5 " + new string('r', AdminCommands.MaxTextLength + 1));
        Refused("/announce " + new string('a', AdminCommands.MaxTextLength + 1));
        Assert.Equal(AdminCommands.MaxTextLength, Parse("/announce " + new string('a', AdminCommands.MaxTextLength)).Text.Length);
    }
}
