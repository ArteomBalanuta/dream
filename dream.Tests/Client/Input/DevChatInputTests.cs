using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Protocol;

namespace Dream.Tests.Client.Input;

// "/dev ..." is the staging line: the whole line travels as ChatChannel.Command, for the server alone to judge.
public sealed class DevChatInputTests
{
    [Theory]
    [InlineData("/dev spawn wolf 2")]
    [InlineData("/dev heal; hp 5")]
    [InlineData("/dev")]
    public void DevLines_TravelWhole_AsTheCommandChannel(string line)
    {
        Assert.True(ChatInput.TryParse(line, out ClientChatMessage? message, out string? error), error);

        Assert.Equal(new ClientChatMessage(ChatChannel.Command, line), message);
        Assert.True(DevCommandText.IsDevLine(message!.Text));
    }

    [Fact]
    public void TheCommand_IsMatchedInAnyCase()
    {
        Assert.True(ChatInput.TryParse("/DEV heal", out ClientChatMessage? message, out _));

        Assert.Equal(ChatChannel.Command, message!.Channel);
    }

    [Theory]
    [InlineData("/devil heal")]
    [InlineData("/de heal")]
    [InlineData("dev heal")]
    public void NearMisses_AreNotDevLines(string line)
    {
        // Negative: only the exact verb is a server command; a lookalike is unknown, or plain World chat.
        if (ChatInput.TryParse(line, out ClientChatMessage? message, out _))
        {
            Assert.NotEqual(ChatChannel.Command, message!.Channel);
        }
    }

    [Fact]
    public void AnAdminCommand_StillTravelsAsCommand_ButIsNotADevLine()
    {
        Assert.True(ChatInput.TryParse("/kick Bob", out ClientChatMessage? message, out _));
        Assert.Equal(ChatChannel.Command, message!.Channel);
        Assert.False(DevCommandText.IsDevLine(message.Text));
    }
}
