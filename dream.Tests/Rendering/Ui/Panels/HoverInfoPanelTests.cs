using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Dream.Tests.Support;

namespace Dream.Tests.Rendering.Ui.Panels;

public class HoverInfoPanelTests
{
    private static readonly Vector2 Pointer = new(600f, 300f);
    private static readonly UiColor TroughColour = new(0x12, 0x0D, 0x0C, 255);

    internal static HoverInfo EnemyInfo(int hp = 80, int max = 100)
    {
        HoverInfo info = new()
        {
            Target = new HoverTargetId(HoverKind.Enemy, 7),
            Disposition = HoverDisposition.Hostile,
            Title = "Grey wolf",
            Subtitle = "Lv 4",
            BarCount = 1,
            LineCount = 2,
            Hint = "Right click to attack"
        };
        info.Bars[0] = new HoverBar(HoverBarKind.Health, hp, max, hp + " / " + max);
        info.Lines[0] = new HoverLine("Attack", "12");
        info.Lines[1] = new HoverLine("Defence", "3");
        return info;
    }

    internal static HudView ViewFor(HoverInfo? info, HoverKind kind = HoverKind.Enemy, float alpha = 1f, bool visible = true)
    {
        HudView v = StatusBarTestData.View();
        v.HoverInfo = info;
        v.HoverCursorKind = kind;
        v.HoverPanel = new HoverPanelState(visible, alpha, kind);
        return v;
    }

    private static StatusBarFakeInput Input(Vector2? at = null) =>
        new() { Pointer = new UiPointer(at ?? Pointer, false, false, true) };

    private static StatusBarRecordingCanvas Run(HudView v, StatusBarFakeInput? input = null, int width = 1920, int height = 1080, float scale = 0f)
    {
        UiLayout l = UiLayout.Create(width, height, scale);
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table);
        c.SetTextScale(l.TextScale);
        HoverInfoPanel.Build(c, input ?? Input(), v, in l, StatusBarTestData.Table);
        return c;
    }

    private static UiRect PanelRect(StatusBarRecordingCanvas c) =>
        c.Calls.First(x => x.Kind == StatusBarCallKind.NineSlice).Rect;

    [Fact]
    public void DrawsNothing_WhenNotVisibleFadedOrWithoutInfo()
    {
        Assert.Empty(Run(ViewFor(EnemyInfo(), visible: false)).Calls);
        Assert.Empty(Run(ViewFor(EnemyInfo(), alpha: 0f)).Calls);
        Assert.Empty(Run(ViewFor(EnemyInfo(), alpha: -1f)).Calls);
        Assert.Empty(Run(ViewFor(EnemyInfo(), alpha: float.NaN)).Calls);
        Assert.Empty(Run(ViewFor(null)).Calls);
        Assert.NotEmpty(Run(ViewFor(EnemyInfo())).Calls);
    }

    [Fact]
    public void DrawsNothing_ForANonFinitePointer()
    {
        Assert.Empty(Run(ViewFor(EnemyInfo()), Input(new Vector2(float.NaN, 5f))).Calls);
    }

    [Fact]
    public void SkipsWhenTheBatchIsNearlyFull()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        LowRoomCanvas c = new(StatusBarTestData.Table);
        HoverInfoPanel.Build(c, Input(), ViewFor(EnemyInfo()), in l, StatusBarTestData.Table);
        Assert.Equal(0, c.Count);
    }

    // Measured, not restated: the fullest panel there can be (every bar and line, every string a long run of a
    // one-pixel glyph, so the width never cuts it first) drawn into a real batch at every UI scale emits no more
    // quads than WorstCaseQuads. Without the per-string glyph cap this fails: 200 dots fit the panel's width.
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    public void TheFullestPanel_InARealBatch_StaysWithinTheWorstCase(float scale)
    {
        UiGlyphTable glyphs = BlockGlyphs.Create();
        // A drawn glyph box (so no scale culls it) with a one-pixel advance: 200 of them fit the panel's width.
        glyphs.Set(UiTextStyle.Body, '.', new UiGlyph(0f, 2f, 6f, 11f, 0f, 0f, 1f, 1f, 1f));
        UiBatch batch = new(glyphs);
        UiLayout l = UiLayout.Create(1920, 1080, scale);
        batch.BeginFrame(in l);
        batch.TextScale = l.TextScale;

        string dots = new('.', 200);
        HoverInfo info = new()
        {
            Target = new HoverTargetId(HoverKind.Enemy, 7),
            Disposition = HoverDisposition.Hostile,
            Title = dots,
            Subtitle = dots,
            Hint = dots,
            BarCount = HoverInfo.MaxBars,
            LineCount = HoverInfo.MaxLines
        };
        for (int n = 0; n < HoverInfo.MaxBars; n++)
        {
            info.Bars[n] = new HoverBar(HoverBarKind.Health, 50, 100, dots);
        }

        for (int n = 0; n < HoverInfo.MaxLines; n++)
        {
            info.Lines[n] = new HoverLine(".", dots);
        }

        int before = batch.QuadCount;
        HoverInfoPanel.Build(batch, Input(), ViewFor(info), in l, StatusBarTestData.Table);
        int used = batch.QuadCount - before;

        Assert.True(used > 4 * HoverInfoPanel.MaxGlyphsPerRow, $"the panel's text must have drawn (used {used} quads at layout scale {l.Scale})");
        Assert.True(used <= HoverInfoPanel.WorstCaseQuads, $"panel emitted {used} quads, worst case is {HoverInfoPanel.WorstCaseQuads}");
        Assert.True(HoverInfoPanel.ReserveQuads >= HoverInfoPanel.WorstCaseQuads + HoverInfoPanel.DownstreamQuads);
    }

    [Fact]
    public void DrawsAtTheReserveAndSkipsOneBelow()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        LowRoomCanvas at = new(StatusBarTestData.Table, HoverInfoPanel.ReserveQuads);
        HoverInfoPanel.Build(at, Input(), ViewFor(EnemyInfo()), in l, StatusBarTestData.Table);
        Assert.True(at.Count > 0);
        LowRoomCanvas below = new(StatusBarTestData.Table, HoverInfoPanel.ReserveQuads - 1);
        HoverInfoPanel.Build(below, Input(), ViewFor(EnemyInfo()), in l, StatusBarTestData.Table);
        Assert.Equal(0, below.Count);
    }

    [Fact]
    public void ACleared_Target_DrawsNothing()
    {
        HoverInfo cleared = EnemyInfo();
        cleared.Target = default;
        Assert.Empty(Run(ViewFor(cleared)).Calls);
    }

    [Fact]
    public void WillDraw_MatchesBuild()
    {
        HoverInfo cleared = EnemyInfo();
        cleared.Target = default;
        Assert.True(HoverInfoPanel.WillDraw(new HoverPanelState(true, 1f, HoverKind.Enemy), EnemyInfo()));
        Assert.False(HoverInfoPanel.WillDraw(new HoverPanelState(false, 1f, HoverKind.Enemy), EnemyInfo()));
        Assert.False(HoverInfoPanel.WillDraw(new HoverPanelState(true, 0f, HoverKind.Enemy), EnemyInfo()));
        Assert.False(HoverInfoPanel.WillDraw(new HoverPanelState(true, float.NaN, HoverKind.Enemy), EnemyInfo()));
        Assert.False(HoverInfoPanel.WillDraw(new HoverPanelState(true, float.PositiveInfinity, HoverKind.Enemy), EnemyInfo()));
        Assert.False(HoverInfoPanel.WillDraw(new HoverPanelState(true, 1f, HoverKind.Enemy), null));
        Assert.False(HoverInfoPanel.WillDraw(new HoverPanelState(true, 1f, HoverKind.Enemy), cleared));
    }

    private sealed class LowRoomCanvas(UiSpriteTable table, int headroom = -1) : IUiCanvas
    {
        private readonly StatusBarRecordingCanvas _inner = new(table, recording: false);

        public int Count => _inner.Count;

        public int QuadHeadroom => headroom < 0 ? HoverInfoPanel.ReserveQuads - 1 : headroom;

        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) => _inner.Sprite(in s, dst, tint);

        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) => _inner.SpriteClipped(in s, dst, srcNormalized, tint);

        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) => _inner.NineSlice(in s, dst, tint);

        public void Tile(in UiSprite s, UiRect dst, UiColor tint) => _inner.Tile(in s, dst, tint);

        public void Solid(UiRect dst, UiColor color) => _inner.Solid(dst, color);

        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) => _inner.MaskGlyph(in s, dst, fill, rim);

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => _inner.Text(text, pos, style, color);

        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => _inner.MeasureText(text, style);

        public void PushClip(UiRect r) => _inner.PushClip(r);

        public void PopClip() => _inner.PopClip();
    }

    [Fact]
    public void NeverTouchesTheInputWidgets()
    {
        CountingInput input = new() { Pointer = new UiPointer(Pointer, false, false, true) };
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table);
        HoverInfoPanel.Build(c, input, ViewFor(EnemyInfo()), in l, StatusBarTestData.Table);

        Assert.NotEmpty(c.Calls);
        Assert.Equal(0, input.WidgetCalls);
    }

    private sealed class CountingInput : IUiInput
    {
        public int WidgetCalls { get; private set; }

        public UiPointer Pointer { get; set; }

        public int ActiveDragPayload => -1;

        public bool ModalActive { get; set; }

        public bool Button(int id, UiRect r) { WidgetCalls++; return false; }

        public bool Hovered(int id, UiRect r) { WidgetCalls++; return false; }

        public bool RightClicked(int id, UiRect r) { WidgetCalls++; return false; }

        public bool BeginDrag(int id, UiRect r, int payload) { WidgetCalls++; return false; }

        public bool TryEndDrag(out int payload, out Vector2 dropPosition)
        {
            WidgetCalls++;
            payload = -1;
            dropPosition = default;
            return false;
        }
    }

    [Fact]
    public void ShowsTitleSubtitleBarLinesAndHint()
    {
        StatusBarRecordingCanvas c = Run(ViewFor(EnemyInfo()));
        string[] texts = [.. c.Texts.Select(t => t.Text!)];

        foreach (string want in new[] { "Grey wolf", "Lv 4", "80 / 100", "Attack", "12", "Defence", "3", "Right click to attack" })
        {
            Assert.Contains(want, texts);
        }
    }

    [Theory]
    [InlineData(HoverDisposition.Hostile, 0xB8, 0x4A, 0x42)]
    [InlineData(HoverDisposition.Friendly, 0xE6, 0xDD, 0xC4)]
    [InlineData(HoverDisposition.Party, 0x7F, 0xC4, 0x9E)]
    public void TitleColour_FollowsTheDisposition(HoverDisposition d, int r, int g, int b)
    {
        HoverInfo info = EnemyInfo();
        info.Disposition = d;
        UiColor title = TitleCall(Run(ViewFor(info)), "Grey wolf").Color;
        Assert.Equal(new UiColor((byte)r, (byte)g, (byte)b, 255), title);
    }

    [Theory]
    [InlineData(ItemTier.Common)]
    [InlineData(ItemTier.Uncommon)]
    [InlineData(ItemTier.Rare)]
    public void ItemTitle_UsesTheTierColour(ItemTier tier)
    {
        HoverInfo info = EnemyInfo();
        info.Disposition = HoverDisposition.Item;
        info.Tier = tier;
        Assert.Equal(ItemTierStyle.ColorOf(tier), TitleCall(Run(ViewFor(info, HoverKind.GroundItem)), "Grey wolf").Color);
    }

    [Fact]
    public void DefeatedTitleIsGrey_AndItsBarIsEmpty()
    {
        HoverInfo info = EnemyInfo();
        info.Defeated = true;
        StatusBarRecordingCanvas c = Run(ViewFor(info));
        Assert.Equal(ItemTierStyle.Dim, TitleCall(c, "Grey wolf").Color);
        Assert.Empty(FillRects(c));
    }

    private static StatusBarCall TitleCall(StatusBarRecordingCanvas c, string title) =>
        c.Texts.Last(t => t.Text == title);

    private static List<StatusBarCall> FillRects(StatusBarRecordingCanvas c)
    {
        UiColor fill = HoverInfoPanel.BarFill(HoverBarKind.Health);
        return [.. c.Calls.Where(x => x.Kind == StatusBarCallKind.Solid && x.Color == fill)];
    }

    [Theory]
    [InlineData(100, 100, 1f)]
    [InlineData(50, 100, 0.5f)]
    [InlineData(1, 100, 0.01f)]
    [InlineData(0, 100, 0f)]
    [InlineData(150, 100, 1f)]
    [InlineData(-5, 100, 0f)]
    [InlineData(5, 0, 0f)]
    public void BarFill_IsCurrentOverMaxClamped(int cur, int max, float expected)
    {
        StatusBarRecordingCanvas c = Run(ViewFor(EnemyInfo(cur, max)));
        List<StatusBarCall> fills = FillRects(c);
        UiRect trough = Assert.Single(c.Calls.Where(x => x.Kind == StatusBarCallKind.Solid && x.Color == TroughColour)).Rect;

        float want = MathF.Round(trough.W * expected);
        if (want <= 0f)
        {
            Assert.Empty(fills);
            return;
        }

        Assert.Equal(want, fills[0].Rect.W, 0.01f);
    }

    [Fact]
    public void BarText_IsCentredOnTheBar()
    {
        StatusBarRecordingCanvas c = Run(ViewFor(EnemyInfo()));
        StatusBarCall text = c.Texts.Last(t => t.Text == "80 / 100");
        UiRect trough = c.Calls.Single(x => x.Kind == StatusBarCallKind.Solid && x.Color == TroughColour).Rect;
        float width = "80 / 100".Length * 7f * c.TextScale;
        float centre = trough.X + (trough.W * 0.5f);
        Assert.InRange(text.TextPos.X + (width * 0.5f), centre - 1f, centre + 1f);
        Assert.InRange(text.TextPos.Y, trough.Y, trough.Bottom);
    }

    [Fact]
    public void AlphaMultipliesEveryColour()
    {
        StatusBarRecordingCanvas full = Run(ViewFor(EnemyInfo(), alpha: 1f));
        StatusBarRecordingCanvas half = Run(ViewFor(EnemyInfo(), alpha: 0.5f));

        Assert.Equal(full.Calls.Count, half.Calls.Count);
        for (int n = 0; n < full.Calls.Count; n++)
        {
            Assert.InRange(half.Calls[n].Color.A, (full.Calls[n].Color.A / 2) - 1, (full.Calls[n].Color.A / 2) + 1);
        }
    }

    [Fact]
    public void LongServerStrings_AreClippedToThePanelWidth()
    {
        HoverInfo info = EnemyInfo();
        info.Title = new string('W', 400);
        info.Subtitle = new string('x', 400);
        info.Hint = new string('h', 400);
        info.Lines[0] = new HoverLine(new string('L', 200), new string('V', 200));
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = Run(ViewFor(info));
        UiRect panel = PanelRect(c);

        foreach (StatusBarCall t in c.Texts)
        {
            float width = t.Text!.Length * 7f * c.TextScale;
            Assert.True(t.TextPos.X >= panel.X, t.Text);
            Assert.True(t.TextPos.X + width <= panel.Right + UiText.ShadowPx(in l) + 0.5f, t.Text);
        }
    }

    [Fact]
    public void WidthIsFixedPerKind_WhateverTheNumbers()
    {
        float Width(HoverInfo info) => PanelRect(Run(ViewFor(info))).W;

        Assert.Equal(Width(EnemyInfo(80, 100)), Width(EnemyInfo(1234567, 2345678)));
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        Assert.Equal(HoverInfoPanel.EnemyWidthUnits * l.Scale, Width(EnemyInfo()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PanelStaysOnScreen_AtEveryScale(int scale)
    {
        int vw = Math.Max(1920, 640 * scale);
        int vh = Math.Max(1080, 480 * scale);
        foreach (Vector2 p in new[] { new Vector2(2f, 2f), new Vector2(vw - 20f, 2f), new Vector2(2f, vh - 80f), new Vector2(vw - 10f, vh - 10f) })
        {
            UiRect panel = PanelRect(Run(ViewFor(EnemyInfo()), Input(p), vw, vh, scale));
            Assert.True(panel.X >= 0f && panel.Y >= 0f && panel.Right <= vw && panel.Bottom <= vh, $"{p} scale {scale}: {panel}");
        }
    }

    [Fact]
    public void ArtMissing_FallsBackToASolidPanel()
    {
        Assert.True(UiSpriteTable.TryLoadPages(Dream.Assets.AssetPaths.UiDirectory, [UiPage.Window], out UiSpriteTable? noHud, out string error), error);
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = new(noHud!);
        HoverInfoPanel.Build(c, Input(), ViewFor(EnemyInfo()), in l, noHud!);
        Assert.NotEmpty(c.Texts);
    }

    [Fact]
    public void HundredBuilds_AllocateNothingOnceWarm()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording: false);
        StatusBarFakeInput input = Input();
        HudView v = ViewFor(EnemyInfo());
        for (int n = 0; n < 5; n++)
        {
            HoverInfoPanel.Build(c, input, v, in l, StatusBarTestData.Table);
        }

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            for (int n = 0; n < 100; n++)
            {
                HoverInfoPanel.Build(c, input, v, in l, StatusBarTestData.Table);
            }
        });

        Assert.Equal(0L, bytes);
    }

    private static readonly string[] ForbiddenImports = ["using Dream.Simulation", "using Dream.Client.Replication", "using Dream.Client.Hover"];

    private static bool ImportsAForbiddenLayer(string source) => ForbiddenImports.Any(source.Contains);

    // The panel is presentation only: it must not know the simulation, the replication layer or the hover layer.
    [Fact]
    public void Source_DoesNotImportTheSimulationReplicationOrHoverLayers()
    {
        string source = File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream", "Rendering", "Ui", "Panels", "HoverInfoPanel.cs"));
        Assert.False(ImportsAForbiddenLayer(source));
    }

    // Synthetic negative: the boundary check can still fail.
    [Theory]
    [InlineData("using Dream.Simulation.Combat;")]
    [InlineData("using Dream.Client.Replication;")]
    [InlineData("using Dream.Client.Hover;")]
    public void TheBoundaryCheck_CatchesAForbiddenImport(string line) =>
        Assert.True(ImportsAForbiddenLayer("using OpenTK.Mathematics;" + Environment.NewLine + line));
}
