using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// The chat panel: a System message splits on '\n' into stored lines, the closed panel is the old
// eight cut lines, and the open (Typing) panel is wider, wraps at spaces and shows up to 24 rows.
public class ChatPanelTests
{
    private static readonly UiColor PanelFill = new(0x0B, 0x07, 0x06, 170);

    private static UiLayout Layout(int w = 1920, int h = 1080) => UiLayout.Create(w, h, 0f);

    private static StatusBarRecordingCanvas Canvas(in UiLayout l, bool recording = true) =>
        new(StatusBarTestData.Table, recording) { TextScale = l.TextScale };

    private static StatusBarRecordingCanvas Draw(ChatHudState chat, in UiLayout l)
    {
        StatusBarRecordingCanvas c = Canvas(in l);
        ChatPanel.Build(c, StatusBarTestData.View(), in l, chat);
        return c;
    }

    private static float ClosedWidth(in UiLayout l) =>
        MathF.Max(MathF.Min(l.ViewportWidth * 0.45f, 150f * l.Scale), MathF.Min(l.ViewportWidth, 200f));

    private static float OpenWidth(in UiLayout l) =>
        MathF.Min(l.ViewportWidth - 2f * MathF.Round(6f * l.Scale), MathF.Max(ClosedWidth(in l), 340f * l.Scale));

    private static UiRect ListPanel(StatusBarRecordingCanvas c) =>
        Assert.Single(c.Calls, k => k.Kind == StatusBarCallKind.Solid && k.Color == PanelFill).Rect;

    // Body-pass text rows (shadow pass is alpha 200), without the "[Server] " marks and the prompt.
    private static List<StatusBarCall> Rows(StatusBarRecordingCanvas c) =>
        [.. c.Texts.Where(t => t.Color.A == 255 && t.Text != ChatPanel.ServerMark && t.Text != ">" && t.Text!.Length > 0)];

    private static ChatHudState Open(params (ChatChannel Channel, string Text)[] lines)
    {
        ChatHudState chat = new() { Typing = true };
        foreach ((ChatChannel channel, string text) in lines)
        {
            chat.AddServerMessage(channel, "Aldric", text);
        }

        return chat;
    }

    private static List<string> Lines(ChatHudState chat) => [.. Enumerable.Range(0, chat.LineCount).Select(i => chat.GetLine(i).Text)];

    // ---- splitting -------------------------------------------------------------------------

    [Fact]
    public void ASystemMessage_SplitsOnNewline_AndDropsCarriageReturnsAndEmptyPieces()
    {
        ChatHudState chat = new();
        chat.AddServerMessage(ChatChannel.System, "", "a\nb\r\nc");
        Assert.Equal(["a", "b", "c"], Lines(chat));

        ChatHudState sparse = new();
        sparse.AddServerMessage(ChatChannel.System, "", "\n\na\n\r\n\nb\n");
        Assert.Equal(["a", "b"], Lines(sparse));
        for (int i = 0; i < sparse.LineCount; i++)
        {
            Assert.Equal(ChatChannel.System, sparse.GetLine(i).Channel);
        }
    }

    [Fact]
    public void ASystemMessage_IsCutAtSixteenLines()
    {
        ChatHudState chat = new();
        chat.AddServerMessage(ChatChannel.System, "", string.Join('\n', Enumerable.Range(0, 40).Select(i => "line" + i)));

        Assert.Equal(ChatHudState.MaxLinesPerMessage, chat.LineCount);
        Assert.Equal("line0", chat.GetLine(0).Text);
        Assert.Equal("line15", chat.GetLine(15).Text);
    }

    [Fact]
    public void EachSystemPiece_IsSanitisedAndClamped()
    {
        ChatHudState chat = new();
        chat.AddServerMessage(ChatChannel.System, "", "ok\u0007x\n" + new string('z', 1000));

        Assert.Equal("okx", chat.GetLine(0).Text);
        Assert.Equal(ChatHudState.MaxTextChars, chat.GetLine(1).Text.Length);
    }

    [Theory]
    [InlineData(ChatChannel.Say)]
    [InlineData(ChatChannel.World)]
    [InlineData(ChatChannel.Whisper)]
    [InlineData(ChatChannel.Party)]
    public void PlayerChannels_NeverSplit(ChatChannel channel)
    {
        ChatHudState chat = new();
        chat.AddServerMessage(channel, "Aldric", "one\ntwo\r\nthree");

        Assert.Equal(1, chat.LineCount);
        Assert.DoesNotContain('\n', chat.GetLine(0).Text);
        Assert.DoesNotContain('\r', chat.GetLine(0).Text);
    }

    // ---- closed panel ----------------------------------------------------------------------

    [Fact]
    public void ClosedPanel_StaysEightCutLines_AtTheClosedWidth()
    {
        UiLayout l = Layout();
        ChatHudState chat = new();
        for (int i = 0; i < 12; i++)
        {
            chat.AddServerMessage(ChatChannel.World, "Aldric", "number " + i + " " + new string('w', 120));
        }

        StatusBarRecordingCanvas c = Draw(chat, in l);
        UiRect panel = ListPanel(c);
        List<StatusBarCall> rows = Rows(c);

        Assert.Equal(ChatHudState.VisibleLines, rows.Count);
        Assert.Equal(ClosedWidth(in l), panel.W, 3);
        Assert.All(rows, r => Assert.True(c.MeasureText(r.Text!, UiTextStyle.Body) <= panel.W));
        Assert.Contains("number 11", rows[^1].Text);
    }

    // ---- open panel ------------------------------------------------------------------------

    [Fact]
    public void OpenPanel_At1080p_IsWide_HoldsAThirteenLineHelpAndHistory_AndStaysBelowTheTopMargin()
    {
        UiLayout l = Layout();
        ChatHudState chat = Open((ChatChannel.World, "the road is watched"), (ChatChannel.Say, "hold here"));
        chat.AddServerMessage(ChatChannel.System, "", string.Join('\n', Enumerable.Range(1, 13).Select(i => "/cmd" + i + " does a thing")));

        StatusBarRecordingCanvas c = Draw(chat, in l);
        UiRect panel = ListPanel(c);
        List<StatusBarCall> rows = Rows(c);

        Assert.Equal(OpenWidth(in l), panel.W, 3);
        Assert.True(panel.W > ClosedWidth(in l));
        Assert.Equal(15, rows.Count);
        Assert.EndsWith("the road is watched", rows[0].Text);
        Assert.Equal("/cmd13 does a thing", rows[^1].Text);
        Assert.True(panel.Y >= 6f * l.Scale);
        Assert.Equal(13, c.Texts.Count(t => t.Text == ChatPanel.ServerMark && t.Color.A == 255));
    }

    [Fact]
    public void OpenPanel_WrapsAtASpace_IndentsTheContinuationToTheBodyColumn_AndMarksOnlyTheFirstRow()
    {
        UiLayout l = Layout();
        string words = string.Join(' ', Enumerable.Range(0, 30).Select(i => "word" + i));
        ChatHudState chat = Open();
        chat.AddServerMessage(ChatChannel.System, "", words);

        StatusBarRecordingCanvas c = Draw(chat, in l);
        UiRect panel = ListPanel(c);
        List<StatusBarCall> rows = Rows(c);

        Assert.True(rows.Count > 2);
        float markW = c.MeasureText(ChatPanel.ServerMark, UiTextStyle.Body);
        float pad = MathF.Max(2f, MathF.Round(2.5f * l.Scale));
        Assert.Equal(1, c.Texts.Count(t => t.Text == ChatPanel.ServerMark && t.Color.A == 255));
        Assert.All(rows, r => Assert.Equal(panel.X + pad + markW, r.TextPos.X, 3));
        Assert.All(rows, r => Assert.True(markW + c.MeasureText(r.Text!, UiTextStyle.Body) <= panel.W - 2f * pad + 0.01f));
        // Broke at spaces only: the rows re-join to the original text, and none starts or ends with a space.
        Assert.Equal(words, string.Join(' ', rows.Select(r => r.Text)));
        Assert.All(rows, r => Assert.True(r.Text!.Length > 0 && r.Text[0] != ' ' && r.Text[^1] != ' '));
    }

    [Fact]
    public void OpenPanel_HardBreaksAWordWithNoSpace_AndAlwaysTakesAtLeastOneChar()
    {
        UiLayout l = Layout();
        ChatHudState chat = Open((ChatChannel.World, new string('w', 200)));

        StatusBarRecordingCanvas c = Draw(chat, in l);
        UiRect panel = ListPanel(c);
        List<StatusBarCall> rows = Rows(c);

        // The stored line is "Aldric: " + 200 w; every char survives the breaks.
        string stored = chat.GetLine(0).Text;
        Assert.True(rows.Count >= 2);
        Assert.All(rows, r => Assert.True(r.Text!.Length >= 1 && c.MeasureText(r.Text, UiTextStyle.Body) <= panel.W));
        Assert.Equal(stored.Replace(" ", string.Empty), string.Concat(rows.Select(r => r.Text)).Replace(" ", string.Empty));
    }

    [Fact]
    public void OpenPanel_PromptFrame_IsAsWideAsTheLogPanel_AtTheSameX()
    {
        UiLayout l = Layout();
        StatusBarRecordingCanvas c = Draw(Open((ChatChannel.World, "hello")), in l);

        UiRect prompt = Assert.Single(c.Calls, k => k.Kind == StatusBarCallKind.Solid && k.Color == new UiColor(0x0B, 0x07, 0x06, 235)).Rect;
        UiRect panel = ListPanel(c);

        Assert.Equal(panel.X, prompt.X, 3);
        Assert.Equal(panel.W, prompt.W, 3);
        Assert.Equal(OpenWidth(in l), prompt.W, 3);
    }

    [Fact]
    public void OpenPanel_HardBreak_NeverSplitsASurrogatePair()
    {
        UiLayout l = Layout();
        ChatHudState chat = Open((ChatChannel.Say, new string('a', 3) + string.Concat(Enumerable.Repeat("😀", 60))));

        StatusBarRecordingCanvas c = Draw(chat, in l);
        List<StatusBarCall> rows = Rows(c);

        Assert.True(rows.Count >= 2);
        Assert.All(rows, r =>
        {
            Assert.False(char.IsLowSurrogate(r.Text![0]), "row starts with a low surrogate");
            Assert.False(char.IsHighSurrogate(r.Text![^1]), "row ends with a high surrogate");
        });
    }

    [Fact]
    public void ASystemMessage_OfMostlyNewlines_IsScannedOnlyUpToTheCap()
    {
        ChatHudState chat = new();
        string text = new string((char)10, ChatHudState.MaxPiecesScanned) + "late";
        chat.AddServerMessage(ChatChannel.System, "", "early" + text);
        Assert.Equal(["early"], Lines(chat));
    }

    [Fact]
    public void OpenPanel_WithARowNarrowerThanOneGlyph_StillTerminatesAndTakesOneCharPerRow()
    {
        // A viewport so small that the body column is narrower than a glyph.
        UiLayout l = Layout(640, 480);
        ChatHudState chat = Open((ChatChannel.World, "abcdef"));
        StatusBarRecordingCanvas c = Canvas(in l);
        c.TextScale = 200f;

        ChatPanel.Build(c, StatusBarTestData.View(), in l, chat);

        Assert.All(Rows(c), r => Assert.True(r.Text!.Length >= 1));
    }

    [Fact]
    public void OpenPanel_NeverShowsMoreThanTwentyFourRows_AndKeepsTheNewestLines()
    {
        UiLayout l = Layout();
        ChatHudState chat = Open();
        for (int i = 0; i < 60; i++)
        {
            chat.AddServerMessage(ChatChannel.World, "Aldric", "message " + i);
        }

        StatusBarRecordingCanvas c = Draw(chat, in l);
        List<StatusBarCall> rows = Rows(c);

        Assert.Equal(ChatHudState.OpenVisibleRows, rows.Count);
        Assert.Contains("message 59", rows[^1].Text);
        float rowH = PanelText.Height(c, UiTextStyle.Body) + UiText.ShadowPx(in l);
        float pad = MathF.Max(2f, MathF.Round(2.5f * l.Scale));
        Assert.Equal(ChatHudState.OpenVisibleRows * rowH + 2f * pad, ListPanel(c).H, 2);
    }

    [Fact]
    public void OpenPanel_WhenTheOldestLineDoesNotFit_ShowsOnlyItsLastRows()
    {
        UiLayout l = Layout();
        ChatHudState chat = Open();
        chat.AddServerMessage(ChatChannel.World, "Aldric", string.Join(' ', Enumerable.Range(0, 120).Select(i => "t" + i)));
        for (int i = 0; i < 23; i++)
        {
            chat.AddServerMessage(ChatChannel.World, "Aldric", "short " + i);
        }

        List<StatusBarCall> rows = Rows(Draw(chat, in l));

        Assert.Equal(ChatHudState.OpenVisibleRows, rows.Count);
        Assert.Equal("Aldric: short 22", rows[^1].Text);
        // One row of the long line is left, and it is the last one of it.
        Assert.EndsWith(chat.GetLine(0).Text.Split(' ')[^1], rows[0].Text);
        Assert.DoesNotContain("Aldric:", rows[0].Text);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(640, 480)]
    public void OpenPanel_StaysInsideTheViewport_AtSmallSizes(int w, int h)
    {
        UiLayout l = Layout(w, h);
        ChatHudState chat = Open();
        for (int i = 0; i < 30; i++)
        {
            chat.AddServerMessage(ChatChannel.System, "", "line " + i + " " + string.Join(' ', Enumerable.Repeat("filler", 12)));
        }

        StatusBarRecordingCanvas c = Draw(chat, in l);
        UiRect panel = ListPanel(c);

        Assert.True(panel.X >= 0f && panel.Right <= l.ViewportWidth);
        Assert.True(panel.Y >= 6f * l.Scale - 0.01f, $"top {panel.Y}");
        Assert.True(panel.Bottom <= l.Bar.Y);
        Assert.All(c.Calls.Where(k => k.Kind == StatusBarCallKind.Solid), k => Assert.True(k.Rect.Right <= l.ViewportWidth + 0.01f));
    }

    [Fact]
    public void OpenPanel_WidthFollowsTheFormula_AtThreeSizes()
    {
        foreach ((int w, int h) in new[] { (1920, 1080), (1280, 720), (640, 480) })
        {
            UiLayout l = Layout(w, h);

            Assert.Equal(OpenWidth(in l), ListPanel(Draw(Open((ChatChannel.World, "hello")), in l)).W, 3);
        }
    }

    [Fact]
    public void BuildWhileTypingWithWrappedLines_AllocatesNothingOnceWarm()
    {
        UiLayout l = Layout();
        ChatHudState chat = Open();
        chat.AddServerMessage(ChatChannel.System, "", string.Join('\n', Enumerable.Range(0, 12).Select(i => "/cmd" + i + " " + string.Join(' ', Enumerable.Repeat("explanation", 20)))));
        chat.AddServerMessage(ChatChannel.Say, "Aldric", new string('w', 200));
        StatusBarRecordingCanvas c = Canvas(in l, recording: false);
        var view = StatusBarTestData.View();
        for (int n = 0; n < 3; n++)
        {
            ChatPanel.Build(c, view, in l, chat);
        }

        long bytes = AllocationAssert.AtMost(0, () => ChatPanel.Build(c, view, in l, chat));

        Assert.Equal(0L, bytes);
    }
}
