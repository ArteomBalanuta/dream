using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public sealed class CharacterWindowTests : IDisposable
{
    private readonly InventoryScene _scene = new();

    public void Dispose() => _scene.Dispose();

    private static HudStatusSnapshot Status(int baseStrength, int strength, float baseSpeed, float speed, int armor) =>
        new(
            CurrentHealth: 50,
            MaxHealth: 100,
            InteractionMode: default,
            MinDamage: 1,
            MaxDamage: 2,
            Strength: strength,
            AttackSpeed: speed,
            Vitality: 7,
            Level: 3,
            Experience: 10,
            ExperienceToNextLevel: 100,
            BaseStrength: baseStrength,
            BaseAttackSpeed: baseSpeed,
            BaseVitality: 7,
            Armor: armor);

    private void Fill(HudStatusSnapshot status)
    {
        _scene.View.Status = status;
        _scene.View.StatRows = CharacterPanelModel.BuildStatRows(status);
    }

    [Fact]
    public void Open_DrawsTheCombineFrameLeftOfTheInventoryWithNoCombineUi()
    {
        Fill(Status(10, 12, 1f, 1f, 0));

        _scene.Frame();

        Assert.Single(_scene.Canvas.Named("win.crown.combine"));
        Assert.Single(_scene.Canvas.Named("win.slab.combine"));
        Assert.Empty(_scene.Canvas.Named("glyph.combine.normal"));
        Assert.Empty(_scene.Canvas.Named("btn.plate44x32.normal"));
        Assert.Equal(_scene.Layout.Character.X, _scene.Canvas.Named("win.slab.combine").Single().Rect.X - (12f * _scene.Layout.Scale), 3);
        Assert.True(_scene.Layout.Character.Right <= _scene.Layout.Inventory.X + 0.001f);
        Assert.True(_scene.Canvas.HasText("Character"));
        Assert.True(_scene.Canvas.HasText("Tester"));
    }

    [Fact]
    public void Rows_ShowBasePlusBonusEqualsEffective()
    {
        Fill(Status(10, 12, 1f, 1.25f, 0));

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Strength"));
        Assert.True(_scene.Canvas.HasText("10 +2 = 12"));
        Assert.True(_scene.Canvas.HasText("Attack Speed"));
        Assert.True(_scene.Canvas.HasText("1 +0.25 = 1.25"));
        Assert.True(_scene.Canvas.HasText("Vitality"));
        Assert.True(_scene.Canvas.HasText("7"));
    }

    [Fact]
    public void Rows_WithoutABonusShowOnlyTheEffectiveValue()
    {
        Fill(Status(10, 10, 1f, 1f, 0));

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("10"));
        Assert.False(_scene.Canvas.Texts.Any(t => t.Text.Contains(" = ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Armor_ShowsItsNote()
    {
        Fill(Status(10, 10, 1f, 1f, 30));

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Armor"));
        Assert.True(_scene.Canvas.HasText("0 +30 = 30"));
        Assert.True(_scene.Canvas.HasText(CharacterPanelModel.ArmorNote));
    }

    [Fact]
    public void Rows_AreClippedAndBalanced()
    {
        Fill(Status(10, 12, 1f, 1f, 0));

        _scene.Frame();

        Assert.Equal(0, _scene.Canvas.ClipDepth);
    }

    [Fact]
    public void NoStatRows_ShowsNoData()
    {
        _scene.View.StatRows = [];

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("No data"));
    }

    [Fact]
    public void ServerStrings_StayLiteralAndShort()
    {
        _scene.View.StatRows =
        [
            new CharacterStatRow(CharacterStat.Strength, "100% %s {0} " + new string('x', 200), 1, 0, 1, "%d", "%n", "%s", "100% note")
        ];

        _scene.Frame();

        Assert.True(_scene.Canvas.Texts.Any(t => t.Text.StartsWith("100% %s {0} ", StringComparison.Ordinal) && t.Text.Length <= 24));
        Assert.True(_scene.Canvas.HasText("%d %n = %s"));
        Assert.True(_scene.Canvas.HasText("100% note"));
    }

    [Fact]
    public void LongPlayerName_IsCut()
    {
        _scene.View.Inventory = new PlayerInventorySnapshot(new string('N', 300), 4, 4, [], null, null);

        _scene.Frame();

        Assert.Contains(_scene.Canvas.Texts, t => t.Text == new string('N', 40));
    }

    [Fact]
    public void NoSnapshot_DrawsNoName()
    {
        _scene.View.Inventory = null;

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Character"));
        Assert.False(_scene.Canvas.HasText("Tester"));
    }

    [Fact]
    public void Closed_DrawsNothing()
    {
        _scene.View.InventoryOpen = false;
        CharacterWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);

        Assert.Empty(_scene.Canvas.Calls);
    }

    [Fact]
    public void CloseButton_ClosesOnce()
    {
        UiRect close = _scene.Layout.FromUnits(_scene.Layout.Character, 19f, 395f, 24f, 24f);
        _scene.Input.Pointer = InventoryScene.Centre(close);
        _scene.Input.LeftClick = true;

        _scene.Frame();

        Assert.Equal(1, _scene.Actions.Closes);
    }

    [Fact]
    public void Row_AllocatesNothingOnceWarm()
    {
        Fill(Status(10, 12, 1f, 1.25f, 30));
        CountingCanvas canvas = new();
        CharacterWindow.Build(canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);

        long bytes = Dream.Tests.Utilities.AllocationAssert.MeasureBytesPerIteration(() =>
        {
            CharacterWindow.Build(canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);
        });

        Assert.Equal(0L, bytes);
    }

    private sealed class CountingCanvas : IUiCanvas
    {
        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }

        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }

        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Solid(UiRect dst, UiColor color) { }

        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) { }

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => text.Length * 6f;

        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => text.Length * 6f;

        public void PushClip(UiRect r) { }

        public void PopClip() { }
    }
}
