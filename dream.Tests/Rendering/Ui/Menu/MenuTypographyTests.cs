using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

// The menu type is sized from ui_scale, so every label has to still fit the plate the art fixed at
// every scale the game reaches. Measuring runs through the real baked font (not a fake 6 px glyph),
// because a label that fits a fake measure and overruns Cinzel's real advances is the whole bug.
public sealed class MenuTypographyTests
{
    public static TheoryData<int, int> Sizes => new() { { 1280, 720 }, { 1920, 1080 }, { 3840, 2160 } };

    private readonly UiSpriteTable _table;
    private readonly MenuSkin _skin;
    private readonly UiInputRouter _router = new();
    private readonly MenuWidgets _widgets = new();

    public MenuTypographyTests()
    {
        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, MenuAtlasLoader.Pages, out UiSpriteTable? table, out string error), error);
        _table = table!;
        _skin = MenuSkin.From(_table);
    }

    // One drawn line: the size it was drawn at and what it measures through the real font.
    private readonly record struct Line(string Text, Vector2 Pos, UiTextStyle Style, float Scale, float Width)
    {
        public float Px => UiText.BakePixels(Style) * Scale;

        public float Right => Pos.X + Width;
    }

    // Records the text of one frame and measures it with the real glyph table underneath.
    private sealed class MeasuringRecorder(UiBatch batch) : IUiCanvas
    {
        public List<Line> Lines { get; } = [];

        public float TextScale => batch.TextScale;

        public void SetTextScale(float scale) => batch.TextScale = scale;

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color)
        {
            float width = batch.MeasureText(text, style);
            Lines.Add(new Line(text.ToString(), pos, style, batch.TextScale, width));
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

    private List<Line> Draw(int width, int height, Func<MenuWidgets, MenuAction> build, out UiLayout layout)
    {
        (UiGlyphTable glyphs, _) = RealFont.Get();
        UiBatch batch = new(glyphs);
        batch.SetSpriteTable(_table);
        layout = UiLayout.Create(width, height, 0f);
        batch.BeginFrame(in layout);
        MeasuringRecorder canvas = new(batch);
        _router.BeginFrame(new UiPointer(new Vector2(-1000f, -1000f), false, false, true));
        _widgets.Begin(canvas, _router, _skin, in layout);
        build(_widgets);
        _router.EndFrame();
        return canvas.Lines;
    }

    // The drawn copy of a line (TextAt draws the shadow first, then the glyphs at the exact position).
    private static Line Find(List<Line> lines, string text)
    {
        List<Line> matches = lines.FindAll(l => l.Text == text);
        Assert.True(matches.Count >= 1, $"'{text}' was not drawn");
        return matches[^1];
    }

    private static void AssertInBox(Line line, UiRect box, float widthFraction, string what)
    {
        Assert.True(line.Px <= box.H + 0.5f, $"{what}: {line.Px:0.##} px tall in a {box.H:0.##} px box");
        Assert.True(line.Width <= box.W * widthFraction + 0.5f, $"{what}: {line.Width:0.##} px wide in {box.W * widthFraction:0.##} px");
        Assert.True(line.Pos.Y >= box.Y - 0.5f && line.Pos.Y + line.Px <= box.Bottom + 0.5f, $"{what}: outside its box vertically");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void MainMenu_ButtonLabelsAndStatusFitTheirPlates(int width, int height)
    {
        const string status = "Session ended: the host closed the world";
        List<Line> lines = Draw(width, height, w => MainMenuPanel.Build(w, false, status, true), out UiLayout layout);

        string[] labels =
        [
            MainMenuPanel.SinglePlayerLabel,
            MainMenuPanel.MultiplayerLabel,
            MainMenuPanel.OptionsLabel,
            MainMenuPanel.CreditsLabel,
            MainMenuPanel.ExitLabel
        ];
        for (int i = 0; i < labels.Length; i++)
        {
            AssertInBox(Find(lines, labels[i]), _widgets.ButtonRect(MainMenuPanel.ButtonsTopUnits, i), 0.92f, labels[i]);
        }

        Line line = Find(lines, status);
        Assert.True(line.Width <= layout.ViewportWidth * 0.9f + 0.5f, $"status line {line.Width:0.##} px wide");
        Assert.Equal(MathF.Max(13f, MathF.Round(MenuWidgets.StatusUnits * layout.Scale)), line.Px, 1);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void PauseMenu_TitleAndButtonsFitThePlate(int width, int height)
    {
        List<Line> lines = Draw(width, height, w => PauseMenuPanel.Build(w, showDeveloper: true), out UiLayout layout);
        float heightUnits = 54f + 5 * MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(8f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = _widgets.Centered(top, PauseMenuPanel.PanelWidthUnits, heightUnits);

        Line title = Find(lines, "PAUSED");
        Assert.Equal(UiTextStyle.Large, title.Style);
        Assert.Equal(MathF.Round(MenuWidgets.PanelTitleUnits * layout.Scale), title.Px, 1);
        Assert.True(title.Width <= panel.W + 0.5f, $"PAUSED {title.Width:0.##} px wide in {panel.W:0.##} px");

        string[] labels =
        [
            PauseMenuPanel.ResumeLabel,
            PauseMenuPanel.OptionsLabel,
            PauseMenuPanel.DeveloperLabel,
            PauseMenuPanel.ExitToMenuLabel,
            PauseMenuPanel.ExitGameLabel
        ];
        for (int i = 0; i < labels.Length; i++)
        {
            UiRect r = _widgets.Centered(top + 52f + i * MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
            AssertInBox(Find(lines, labels[i]), r, 0.92f, labels[i]);
        }
    }

    // The real rows of the Options screen, longest labels and readouts included.
    public static List<MenuOptionRow> OptionRows() =>
    [
        MenuOptionRow.Header("Display"),
        MenuOptionRow.Slider("UI Scale", 1f, 4f, 0.25f, () => 2.25f, _ => { }, _ => "Auto"),
        MenuOptionRow.Slider("Max FPS", 0f, 4f, 1f, () => 2f, _ => { }, _ => "Unlimited"),
        MenuOptionRow.Slider("Anti-Aliasing (restart)", 0f, 3f, 1f, () => 2f, _ => { }, _ => "MSAA 4x"),
        MenuOptionRow.Slider("Damage Text Size", 0.5f, 2f, 0.05f, () => 1f, _ => { }, v => $"{v:0.00}x"),
        MenuOptionRow.Toggle("Admin HUD (F3)", () => true, _ => { }),
        MenuOptionRow.Toggle("Show Frame Time", () => false, _ => { }, isEnabled: () => false)
    ];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Options_RowsFitTheirColumnsAndDoNotOverlap(int width, int height)
    {
        List<MenuOptionRow> rows = OptionRows();
        List<Line> lines = Draw(width, height, w => OptionsPanel.Build(w, rows, backdrop: true), out UiLayout layout);
        float heightUnits = 50f + rows.Count * OptionsPanel.RowHeightUnits + 8f + 2f * MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = _widgets.Centered(top, OptionsPanel.PanelWidthUnits, heightUnits);
        float labelWidth = OptionsPanel.LabelWidthUnits * layout.Scale;

        Line title = Find(lines, "OPTIONS");
        Assert.Equal(UiTextStyle.Large, title.Style);
        Assert.Equal(MathF.Round(MenuWidgets.PanelTitleUnits * layout.Scale), title.Px, 1);
        Assert.True(title.Width <= panel.W + 0.5f, "OPTIONS is wider than its plate");

        for (int i = 0; i < rows.Count; i++)
        {
            UiRect row = _widgets.Inside(panel, 14f, 46f + i * OptionsPanel.RowHeightUnits, OptionsPanel.PanelWidthUnits - 28f, OptionsPanel.RowHeightUnits);
            Line label = Find(lines, rows[i].Label);
            Assert.True(label.Px <= row.H + 0.5f, $"{rows[i].Label}: {label.Px:0.##} px tall in a {row.H:0.##} px row");
            Assert.True(label.Width <= labelWidth + 0.5f, $"{rows[i].Label}: {label.Width:0.##} px wide in a {labelWidth:0.##} px column");
            Assert.True(label.Right <= row.X + labelWidth + 0.5f, $"{rows[i].Label} runs past its column");
        }

        // The slider readouts sit right of the label column and inside the row.
        foreach (string value in new[] { "Auto", "Unlimited", "MSAA 4x", "1.00x" })
        {
            Line line = Find(lines, value);
            UiRect row = new(panel.X + 14f * layout.Scale, line.Pos.Y, (OptionsPanel.PanelWidthUnits - 28f) * layout.Scale, OptionsPanel.RowHeightUnits * layout.Scale);
            Assert.True(line.Pos.X >= row.X + labelWidth, $"{value} overlaps the label column");
            Assert.True(line.Right <= row.Right + 0.5f, $"{value} runs past the plate");
            Assert.True(line.Px <= OptionsPanel.RowHeightUnits * layout.Scale + 0.5f, $"{value} is taller than its row");
        }

        float buttonsTop = 46f + rows.Count * OptionsPanel.RowHeightUnits + 8f;
        float x = (OptionsPanel.PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f;
        AssertInBox(
            Find(lines, OptionsPanel.KeyBindingsLabel),
            _widgets.Inside(panel, x, buttonsTop, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits),
            0.92f,
            OptionsPanel.KeyBindingsLabel);
        AssertInBox(
            Find(lines, OptionsPanel.BackLabel),
            _widgets.Inside(panel, x, buttonsTop + MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits),
            0.92f,
            OptionsPanel.BackLabel);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void KeyBindings_EveryRowFitsItsTwoColumns(int width, int height)
    {
        List<Line> lines = Draw(width, height, w => KeyBindingsPanel.Build(w, backdrop: true), out UiLayout layout);
        float heightUnits = 50f + KeyBindingsPanel.Entries.Length * KeyBindingsPanel.RowHeightUnits + 12f + MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = _widgets.Centered(top, KeyBindingsPanel.PanelWidthUnits, heightUnits);
        float rowH = KeyBindingsPanel.RowHeightUnits * layout.Scale;

        Line title = Find(lines, "KEY BINDINGS");
        Assert.Equal(UiTextStyle.Large, title.Style);
        Assert.Equal(MathF.Round(MenuWidgets.PanelTitleUnits * layout.Scale), title.Px, 1);
        Assert.True(title.Width <= panel.W + 0.5f, "KEY BINDINGS is wider than its plate");

        for (int i = 0; i < KeyBindingsPanel.Entries.Length; i++)
        {
            UiRect row = _widgets.Inside(panel, 18f, 46f + i * KeyBindingsPanel.RowHeightUnits, KeyBindingsPanel.PanelWidthUnits - 36f, KeyBindingsPanel.RowHeightUnits);
            float effectX = row.X + KeyBindingsPanel.EffectColumnUnits * layout.Scale;
            Line input = Find(lines, KeyBindingsPanel.Entries[i].Input);
            Line effect = Find(lines, KeyBindingsPanel.Entries[i].Effect);

            Assert.True(input.Px <= rowH + 0.5f, $"{input.Text}: taller than its row");
            Assert.True(effect.Px <= rowH + 0.5f, $"{effect.Text}: taller than its row");
            Assert.True(input.Right <= effectX + 0.5f, $"{input.Text} runs into the effect column");
            Assert.True(effect.Right <= row.Right + 0.5f, $"{effect.Text} runs past the plate");
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Credits_LinesFitThePlateAndTheirPitch(int width, int height)
    {
        List<Line> lines = Draw(width, height, MainMenuPanel.BuildCredits, out UiLayout layout);
        float heightUnits = 40f + MainMenuPanel.CreditLines.Length * 16f + 56f;
        UiRect panel = _widgets.Centered(MathF.Max(8f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f), 300f, heightUnits);

        foreach (string text in MainMenuPanel.CreditLines)
        {
            if (text.Length == 0)
            {
                continue;
            }

            Line line = Find(lines, text);
            Assert.True(line.Px <= 16f * layout.Scale + 0.5f, $"{text}: {line.Px:0.##} px tall in a {16f * layout.Scale:0.##} px line");
            Assert.True(line.Width <= panel.W + 0.5f, $"{text}: wider than the plate");
        }
    }

    // The engraved face is what the menu is for: the titles and the button words must be Cinzel at
    // every size the game reaches, not the pixel face standing in for it.
    [Theory]
    [MemberData(nameof(Sizes))]
    public void ButtonLabels_UseTheEngravedFace(int width, int height)
    {
        List<Line> lines = Draw(width, height, w => MainMenuPanel.Build(w, false, null, false), out _);

        foreach (string label in new[] { MainMenuPanel.SinglePlayerLabel, MainMenuPanel.OptionsLabel, MainMenuPanel.ExitLabel })
        {
            Assert.Equal(UiTextStyle.Large, Find(lines, label).Style);
        }
    }
}
