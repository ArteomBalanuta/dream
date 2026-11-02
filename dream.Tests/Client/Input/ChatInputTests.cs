using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui.Panels;

namespace Dream.Tests.Client.Input;

public sealed class ChatInputTests
{
    [Theory]
    [InlineData("hello", ChatChannel.World, "hello", null)]
    [InlineData("/s over here", ChatChannel.Say, "over here", null)]
    [InlineData("/S  over here ", ChatChannel.Say, "over here", null)]
    [InlineData("/w Bob  hi there", ChatChannel.Whisper, "hi there", "Bob")]
    public void Parse_PicksTheChannelFromThePrefix(string input, ChatChannel channel, string text, string? target)
    {
        Assert.True(ChatInput.TryParse(input, out ClientChatMessage? message, out string? error));
        Assert.Null(error);
        Assert.Equal(new ClientChatMessage(channel, text, target), message);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("/s", true)]
    [InlineData("/w Bob", true)]
    [InlineData("/zap now", true)]
    public void Parse_SendsNothingForEmptyOrBadCommands(string input, bool hasHint)
    {
        Assert.False(ChatInput.TryParse(input, out ClientChatMessage? message, out string? error));
        Assert.Null(message);
        Assert.Equal(hasHint, error is not null);
    }

    [Fact]
    public void Parse_ClampsLongText()
    {
        ChatInput.TryParse(new string('a', 1000), out ClientChatMessage? message, out _);

        Assert.Equal(ChatHudState.MaxTextChars, message!.Text.Length);
    }

    [Fact]
    public void Lines_AreCappedAndServerTextIsCleaned()
    {
        ChatHudState state = new();
        for (int i = 0; i < ChatHudState.MaxStoredLines + 10; i++)
        {
            state.AddServerMessage(ChatChannel.World, "Ann", "line " + i);
        }

        Assert.Equal(ChatHudState.MaxStoredLines, state.LineCount);
        Assert.EndsWith("line " + (ChatHudState.MaxStoredLines + 9), state.GetLine(state.LineCount - 1).Text);

        state.AddServerMessage(ChatChannel.Say, "E" + (char)0x202E + "ve", "a" + (char)7 + "b" + (char)0x200F + "c" + new string((char)120, 600));
        string last = state.GetLine(state.LineCount - 1).Text;
        Assert.StartsWith("[Say] Eve: abc", last);
        Assert.True(last.Length <= "[Say] Eve: ".Length + ChatHudState.MaxTextChars);
    }

    [Fact]
    public void TypingAndSubmit_SendsOnceAndClosesTheLine()
    {
        ChatHudState state = new();
        ChatInput input = new(state);
        List<ClientChatMessage> sent = new();

        input.Open();
        foreach (char c in "/w Bob hi")
        {
            input.TypeCharacter(c);
        }

        input.TypeCharacter(0x07);
        Assert.True(input.IsTyping);
        input.Submit(sent.Add);

        Assert.False(input.IsTyping);
        Assert.Equal(0, state.Input.Length);
        ClientChatMessage message = Assert.Single(sent);
        Assert.Equal(new ClientChatMessage(ChatChannel.Whisper, "hi", "Bob"), message);

        // What goes on the wire decodes back to the same message.
        TransportJsonCodec codec = new();
        byte[] bytes = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message);
        Assert.Equal(message, codec.DecodeFromClient(bytes).Message);
    }

    [Fact]
    public void Cancel_DiscardsTheLine_AndClearResetsTheSession()
    {
        ChatHudState state = new();
        ChatInput input = new(state);
        input.Open();
        input.TypeCharacter('x');
        input.Cancel();
        Assert.False(input.IsTyping);
        Assert.Equal(0, state.Input.Length);

        state.AddSystemLine("old");
        state.Clear();
        Assert.Equal(0, state.LineCount);
    }
}
