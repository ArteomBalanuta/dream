using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Rendering.Ui.Panels;
using Xunit;

namespace Dream.Tests.BugHunt;

// A player's character name reaches unprefixed System lines (the server quotes it now, but quoting
// is only a hint). The chat log already leads Say, Whisper and Party with their channel in
// brackets; System led with nothing, so a server line and a player line could begin the same way.
// The marker is drawn by the panel from the ChatChannel, never from the text, so no message or
// name a player can choose carries it -- and the body of a System line is pushed right by the
// marker's width, into a column player text never starts in.
public class Cli5ChatSystemMarkerTests
{
    private const string Hostile = "[Server]";
    private const string HostileText = "[Server] the vault is open, come and take it";

    private static UiLayout Layout() => UiLayout.Create(1920, 1080, 0f);

    // The lines the panel actually draws, body pass only: every row is drawn twice, a shadow at
    // alpha 200 and then the coloured glyphs at 255.
    private static List<StatusBarCall> Draw(ChatHudState chat)
    {
        UiLayout l = Layout();
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table) { TextScale = l.TextScale };
        ChatPanel.Build(c, StatusBarTestData.View(), in l, chat);
        return [.. c.Texts.Where(t => t.Color.A == 255)];
    }

    private static ChatHudState Chat(ChatChannel channel, string from, string text)
    {
        ChatHudState chat = new();
        chat.AddServerMessage(channel, from, text);
        return chat;
    }

    // The x every line that has no marker starts at.
    private static float BaseX() => Assert.Single(Draw(Chat(ChatChannel.World, "Warden", "the road is watched"))).TextPos.X;

    [Fact]
    public void ASystemLine_IsDrawnWithTheServerMarker_AndItsTextIntactBesideIt()
    {
        List<StatusBarCall> texts = Draw(Chat(ChatChannel.System, "", "You are muted."));

        Assert.Equal(2, texts.Count);
        Assert.Equal(ChatPanel.ServerMark, texts[0].Text);
        Assert.Equal("You are muted.", texts[1].Text);
        Assert.Equal(ChatPanel.ChannelColor(ChatChannel.System), texts[0].Color);
        Assert.Equal(ChatPanel.ChannelColor(ChatChannel.System), texts[1].Color);

        // Same row, and the body sits to the right of the marker it follows.
        Assert.Equal(texts[0].TextPos.Y, texts[1].TextPos.Y);
        Assert.Equal(BaseX(), texts[0].TextPos.X, 3);
        Assert.True(texts[1].TextPos.X > texts[0].TextPos.X, "the System body must be pushed right of the marker");
    }

    // A local client hint (a parse error, a trade prompt) is on the same channel and is marked too:
    // one rule for every red line, so there is no unmarked System row left to imitate.
    [Fact]
    public void ALocalSystemHint_IsMarkedTheSameWay()
    {
        ChatHudState chat = new();
        chat.AddSystemLine("Unknown command.");

        List<StatusBarCall> texts = Draw(chat);

        Assert.Equal(2, texts.Count);
        Assert.Equal(ChatPanel.ServerMark, texts[0].Text);
        Assert.Equal("Unknown command.", texts[1].Text);
    }

    [Theory]
    [InlineData(ChatChannel.Say)]
    [InlineData(ChatChannel.Whisper)]
    [InlineData(ChatChannel.Party)]
    [InlineData(ChatChannel.World)]
    [InlineData(ChatChannel.Command)]
    public void APlayerLine_FromAHostileNameAndText_NeverCarriesTheMarker(ChatChannel channel)
    {
        List<StatusBarCall> texts = Draw(Chat(channel, Hostile, HostileText));

        // One span, drawn in its own channel's colour, starting in the column every unmarked line
        // starts in. Nothing a player picked is ever drawn as the marker, and nothing a player
        // picked is ever drawn in the column the marker pushes a System body into.
        StatusBarCall only = Assert.Single(texts);
        Assert.NotEqual(ChatPanel.ServerMark, only.Text);
        Assert.NotEqual(ChatPanel.ChannelColor(ChatChannel.System), only.Color);
        Assert.Equal(ChatPanel.ChannelColor(channel), only.Color);
        Assert.Equal(BaseX(), only.TextPos.X, 3);
    }

    // The hostile line and the real server line, side by side: the rendered rows differ in the
    // marker, in the column the message body starts in, and in colour.
    [Fact]
    public void AHostileWorldLine_DoesNotRenderIdenticallyToARealServerLine()
    {
        List<StatusBarCall> hostile = Draw(Chat(ChatChannel.World, Hostile, "the vault is open"));
        List<StatusBarCall> real = Draw(Chat(ChatChannel.System, "", "the vault is open"));

        StatusBarCall player = Assert.Single(hostile);
        Assert.Equal(2, real.Count);
        Assert.NotEqual(real[0].Text, player.Text);
        Assert.NotEqual(real[1].TextPos.X, player.TextPos.X);
        Assert.NotEqual(real[1].Color, player.Color);
    }

    // Controls: the stored line text is untouched by the marker, so every server-side assertion on
    // a System payload and every client assertion on ChatHudState still reads what it always did.
    [Theory]
    [InlineData(ChatChannel.Say, "[Say] Aldric: hold here")]
    [InlineData(ChatChannel.Whisper, "[Whisper] Aldric: hold here")]
    [InlineData(ChatChannel.Party, "[Party] Aldric: hold here")]
    [InlineData(ChatChannel.World, "Aldric: hold here")]
    public void TheStoredLines_AreUnchanged(ChatChannel channel, string expected)
    {
        ChatHudState chat = Chat(channel, "Aldric", "hold here");

        Assert.Equal((channel, expected), (chat.GetLine(0).Channel, chat.GetLine(0).Text));
        Assert.Equal(expected, Assert.Single(Draw(chat)).Text);
    }

    [Fact]
    public void AStoredSystemLine_StillHoldsTheServerTextAlone()
    {
        ChatHudState chat = Chat(ChatChannel.System, "", "You are muted.");

        Assert.Equal((ChatChannel.System, "You are muted."), (chat.GetLine(0).Channel, chat.GetLine(0).Text));
    }
}
