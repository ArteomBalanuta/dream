using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui.Panels;

namespace Dream.Tests.Client.Input;

// M5: the party and trade slash commands. /p is the Party channel, /invite /accept /decline /leave /trade
// are player actions (never chat lines), and the server commands travel as the Command channel for the
// server alone to judge.
public sealed class PartyChatInputTests
{
    [Fact]
    public void SlashP_IsAPartyLine()
    {
        Assert.True(ChatInput.TryParse("/p  push now ", out ClientChatMessage? message, out string? error));
        Assert.Null(error);
        Assert.Equal(new ClientChatMessage(ChatChannel.Party, "push now"), message);

        Assert.False(ChatInput.TryParse("/p", out ClientChatMessage? none, out string? hint));
        Assert.Null(none);
        Assert.NotNull(hint);
    }

    [Theory]
    [InlineData("/invite Bob", "Bob")]
    [InlineData("/INVITE  Bob  extra words", "Bob")]
    [InlineData("/invite \"Two Words\"", "Two Words")]
    public void SlashInvite_IsAPartyInviteIntent(string input, string name)
    {
        Assert.True(ChatInput.TryParseAction(input, out IPlayerActionPayload? payload, out string? error));
        Assert.Null(error);
        Assert.Equal(new PartyInviteIntent(name), payload);
    }

    [Theory]
    [InlineData("/accept", true)]
    [InlineData("/Accept now", true)]
    [InlineData("/decline", false)]
    public void AcceptAndDecline_AnswerTheInvite(string input, bool accept)
    {
        Assert.True(ChatInput.TryParseAction(input, out IPlayerActionPayload? payload, out _));
        Assert.Equal(new PartyRespondIntent(accept), payload);
    }

    [Fact]
    public void LeaveAndTrade_AreActions()
    {
        Assert.True(ChatInput.TryParseAction("/leave", out IPlayerActionPayload? leave, out _));
        Assert.IsType<PartyLeaveIntent>(leave);
        Assert.True(ChatInput.TryParseAction("/trade Bob", out IPlayerActionPayload? trade, out _));
        Assert.Equal(new TradeRequestIntent("Bob"), trade);
    }

    [Theory]
    [InlineData("/invite")]
    [InlineData("/invite   ")]
    [InlineData("/trade")]
    [InlineData("/trade \"\"")]
    public void AMissingName_IsAUsageHint_NotAnAction(string input)
    {
        Assert.True(ChatInput.TryParseAction(input, out IPlayerActionPayload? payload, out string? error));
        Assert.Null(payload);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/s hi")]
    [InlineData("/w Bob hi")]
    [InlineData("/p hi")]
    [InlineData("/kick Bob")]
    [InlineData("")]
    public void EverythingElse_IsNotAnAction(string input)
    {
        Assert.False(ChatInput.TryParseAction(input, out IPlayerActionPayload? payload, out string? error));
        Assert.Null(payload);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("/kick Bob")]
    [InlineData("/ban Bob 30 spamming")]
    [InlineData("/unban Bob")]
    [InlineData("/mute \"Two Words\" 10")]
    [InlineData("/unmute Bob")]
    [InlineData("/announce Restart in five minutes")]
    public void ServerCommands_GoToTheServerAsTheCommandChannel_WholeLine(string input)
    {
        Assert.True(ChatInput.TryParse(input, out ClientChatMessage? message, out string? error));
        Assert.Null(error);
        Assert.Equal(new ClientChatMessage(ChatChannel.Command, input), message);
    }

    [Fact]
    public void Submit_SendsActionsToSendAction_ChatToSend_AndKeepsWhisperAndSay()
    {
        ChatInput input = new(new ChatHudState());
        List<IPlayerActionPayload> actions = [];
        input.SendAction = actions.Add;
        List<ClientChatMessage> chat = [];

        Type("/invite Bob", input);
        input.Submit(chat.Add);
        Type("/trade Bob", input);
        input.Submit(chat.Add);
        Type("/p ready", input);
        input.Submit(chat.Add);
        Type("/s over here", input);
        input.Submit(chat.Add);
        Type("/w Bob hi", input);
        input.Submit(chat.Add);

        Assert.Equal<IPlayerActionPayload>([new PartyInviteIntent("Bob"), new TradeRequestIntent("Bob")], actions);
        Assert.Equal(
            [
                new ClientChatMessage(ChatChannel.Party, "ready"),
                new ClientChatMessage(ChatChannel.Say, "over here"),
                new ClientChatMessage(ChatChannel.Whisper, "hi", "Bob"),
            ],
            chat);
    }

    [Fact]
    public void WithoutASendAction_AnActionCommandSendsNothing_AndAUsageHintIsShown()
    {
        ChatHudState state = new();
        ChatInput input = new(state);
        List<ClientChatMessage> chat = [];

        Type("/leave", input);
        input.Submit(chat.Add);
        Assert.Empty(chat);

        Type("/invite", input);
        input.Submit(chat.Add);
        Assert.Empty(chat);
        Assert.Equal(1, state.LineCount);
        Assert.Contains("/invite", state.GetLine(0).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnknownCommandText_NamesTheNewCommands()
    {
        Assert.False(ChatInput.TryParse("/zap now", out _, out string? error));
        Assert.Equal(ChatInput.UnknownCommandText, error);
        foreach (string command in new[] { "/s", "/w", "/p", "/invite", "/accept", "/decline", "/leave", "/trade" })
        {
            Assert.Contains(command, error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePartyChannel_ShowsItsOwnPrefix_AndColour()
    {
        ChatHudState state = new();
        state.AddServerMessage(ChatChannel.Party, "Alice", "left flank");

        Assert.Equal((ChatChannel.Party, "[Party] Alice: left flank"), (state.GetLine(0).Channel, state.GetLine(0).Text));
        Assert.NotEqual(ChatPanel.ChannelColor(ChatChannel.World), ChatPanel.ChannelColor(ChatChannel.Party));
        Assert.NotEqual(ChatPanel.ChannelColor(ChatChannel.Say), ChatPanel.ChannelColor(ChatChannel.Party));
        Assert.NotEqual(ChatPanel.ChannelColor(ChatChannel.World), ChatPanel.ChannelColor(ChatChannel.Command));
    }

    private static void Type(string text, ChatInput input)
    {
        input.Open();
        foreach (char c in text)
        {
            input.TypeCharacter(c);
        }
    }
}
