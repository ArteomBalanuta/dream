using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

public sealed class LoadingScreenPanelTests
{
    private readonly UiSpriteTable _table;
    private readonly MenuSkin _skin;
    private readonly UiInputRouter _router = new();
    private readonly MenuWidgets _widgets = new();

    public LoadingScreenPanelTests()
    {
        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, MenuAtlasLoader.Pages, out UiSpriteTable? table, out string error), error);
        _table = table!;
        _skin = MenuSkin.From(_table);
    }

    private InventoryFakeCanvas Draw(LoadingScreenState state, int width = 1440, int height = 1024, MenuSkin? skin = null)
    {
        InventoryFakeCanvas canvas = new(_table);
        UiLayout layout = UiLayout.Create(width, height, 0f);
        _router.BeginFrame(new UiPointer(new Vector2(-1000f, -1000f), false, false, true));
        _widgets.Begin(canvas, _router, skin ?? _skin, in layout);
        LoadingScreenPanel.Build(_widgets, in state);
        _router.EndFrame();
        return canvas;
    }

    private static UiColor Fill => new(168, 104, 38, 220);

    [Theory]
    [InlineData(1440, 1024)]
    [InlineData(800, 600)]
    public void Draws_WithAndWithoutArt_AndWithANullTip(int width, int height)
    {
        LoadingScreenState state = new("Starting the local server...", 0.3f, false, null);
        Draw(state, width, height);
        Draw(state, width, height, MenuSkin.From(new UiSpriteTable()));
        InventoryFakeCanvas canvas = Draw(new LoadingScreenState("Waking", 0.1f, false, LoadingScreenPanel.Tips[0]), width, height);
        Assert.Single(canvas.Named(MenuSkin.Backdrop));
        Assert.Single(canvas.Named(MenuSkin.Title));
        Assert.True(canvas.HasText(LoadingScreenPanel.Tips[0]));
    }

    [Fact]
    public void Ready_DrawsThePromptAndNotTheStage()
    {
        InventoryFakeCanvas canvas = Draw(new LoadingScreenState("The world awaits.", 0.2f, true, null, 0.5f));

        Assert.True(canvas.HasText(LoadingScreenPanel.PromptText));
        Assert.False(canvas.HasText("The world awaits."));

        InventoryFakeCanvas busy = Draw(new LoadingScreenState("Working", 0.2f, false));
        Assert.True(busy.HasText("Working"));
        Assert.False(busy.HasText(LoadingScreenPanel.PromptText));
    }

    private static float FillWidth(InventoryFakeCanvas canvas) =>
        canvas.Calls.Where(c => c.Kind == CanvasCallKind.Solid && c.Color == Fill).Select(c => c.Rect.W).DefaultIfEmpty(0f).Max();

    [Fact]
    public void Progress_IsClamped()
    {
        float full = FillWidth(Draw(new LoadingScreenState("x", 1f, false)));
        Assert.True(full > 0f);
        Assert.Equal(full, FillWidth(Draw(new LoadingScreenState("x", 2f, false))), 2);
        Assert.Equal(0f, FillWidth(Draw(new LoadingScreenState("x", -1f, false))), 2);
        Assert.Equal(0f, FillWidth(Draw(new LoadingScreenState("x", float.NaN, false))), 2);
        Assert.Equal(full, FillWidth(Draw(new LoadingScreenState("x", 0f, true))), 2);
        Assert.InRange(FillWidth(Draw(new LoadingScreenState("x", 0.5f, false))), full * 0.49f, full * 0.51f);
    }

    [Fact]
    public void PickTip_WrapsAnySeed()
    {
        Assert.InRange(LoadingScreenPanel.Tips.Length, 6, 10);
        Assert.Equal(LoadingScreenPanel.Tips[0], LoadingScreenPanel.PickTip(0));
        Assert.Equal(LoadingScreenPanel.Tips[1], LoadingScreenPanel.PickTip(LoadingScreenPanel.Tips.Length + 1));
        Assert.Contains(LoadingScreenPanel.PickTip(-1), LoadingScreenPanel.Tips);
        Assert.Contains(LoadingScreenPanel.PickTip(int.MinValue), LoadingScreenPanel.Tips);
    }

    [Theory]
    [InlineData(800, 600)]
    [InlineData(1280, 720)]
    [InlineData(3840, 2160)]
    public void TextFitsInsideThePanelAndTheViewport(int width, int height)
    {
        (UiGlyphTable glyphs, _) = RealFont.Get();
        UiBatch batch = new(glyphs);
        batch.SetSpriteTable(_table);
        UiLayout layout = UiLayout.Create(width, height, 0f);
        string longStage = "Waiting for the previous world to finish saving to disk, please hold on";
        foreach ((string stage, bool ready) in new[] { ("Starting the local server...", false), (longStage, false), ("x", true) })
        {
            batch.BeginFrame(in layout);
            List<(string Text, float Width, float X)> lines = [];
            Recorder canvas = new(batch, lines);
            _router.BeginFrame(new UiPointer(new Vector2(-1000f, -1000f), false, false, true));
            _widgets.Begin(canvas, _router, _skin, in layout);
            LoadingScreenPanel.Build(_widgets, new LoadingScreenState(stage, 0.5f, ready, LoadingScreenPanel.Tips[2]));
            _router.EndFrame();

            UiRect panel = _widgets.Centered(LoadingScreenPanel.PanelTopUnits, LoadingScreenPanel.PanelWidthUnits, LoadingScreenPanel.PanelHeightUnits);
            string shown = ready ? LoadingScreenPanel.PromptText : stage;
            var line = lines.Last(l => l.Text == shown);
            Assert.True(line.X >= panel.X - 0.5f && line.X + line.Width <= panel.Right + 0.5f, $"'{shown}' spans {line.X:0.#}..{line.X + line.Width:0.#} in panel {panel.X:0.#}..{panel.Right:0.#}");
            var tip = lines.Last(l => l.Text == LoadingScreenPanel.Tips[2]);
            Assert.True(tip.Width <= layout.ViewportWidth * 0.9f + 0.5f);
        }
    }

    private sealed class Recorder(UiBatch batch, List<(string Text, float Width, float X)> lines) : IUiCanvas
    {
        public float TextScale => batch.TextScale;

        public void SetTextScale(float scale) => batch.TextScale = scale;

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color)
        {
            float width = batch.MeasureText(text, style);
            lines.Add((text.ToString(), width, pos.X));
            return width;
        }

        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => batch.MeasureText(text, style);

        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }

        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }

        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Solid(UiRect dst, UiColor color) { }

        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) { }

        public void PushClip(UiRect r) { }

        public void PopClip() { }
    }
}
