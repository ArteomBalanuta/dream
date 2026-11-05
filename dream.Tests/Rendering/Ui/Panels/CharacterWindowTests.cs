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

    private static HudStatusSnapshot Status(int baseStrength, int strength, float baseSpeed, float speed, int armor, int level = 3) =>
        new(
            CurrentHealth: 50,
            MaxHealth: 100,
            InteractionMode: default,
            MinDamage: 1,
            MaxDamage: 2,
            Strength: strength,
            AttackSpeed: speed,
            Vitality: 7,
            Level: level,
            Experience: 10,
            ExperienceToNextLevel: 100,
            BaseStrength: baseStrength,
            BaseAttackSpeed: baseSpeed,
            BaseVitality: 7,
            Armor: armor,
            CurrentMana: 30,
            MaxMana: 70,
            MagicMinDamage: 10,
            MagicMaxDamage: 20);

    private void Fill(HudStatusSnapshot status)
    {
        _scene.View.Status = status;
        _scene.View.StatRows = CharacterPanelModel.BuildStatRows(status);
        _scene.View.Sheet = CharacterSheetModel.Build(status);
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
        Assert.True(_scene.Canvas.HasText("Damage"));
        Assert.True(_scene.Canvas.HasText("1-2"));
        Assert.True(_scene.Canvas.HasText("Attack Speed"));
        Assert.True(_scene.Canvas.HasText("1.25/s"));
        Assert.True(_scene.Canvas.HasText("Vitality"));
        Assert.True(_scene.Canvas.HasText("Health"));
        Assert.True(_scene.Canvas.HasText("50/100"));
        Assert.True(_scene.Canvas.HasText("Mana"));
        Assert.True(_scene.Canvas.HasText("30/70"));
        Assert.True(_scene.Canvas.HasText("Magic Damage"));
        Assert.True(_scene.Canvas.HasText("10-20"));
    }

    [Theory]
    [InlineData(6f)]
    [InlineData(7f)]
    [InlineData(8f)]
    public void Rows_NeverTouchTheirValue_AtScale1_25(float charWidth)
    {
        Fill(Status(10, 12, 1f, 1.25f, 0));
        UiLayout layout = UiLayout.Create(1100, 700, 0f);
        Assert.Equal(1.25f, layout.Scale);
        InventoryFakeCanvas canvas = new(_scene.Table) { CharWidth = charWidth };

        CharacterWindow.Build(canvas, _scene.Input, _scene.View, layout, _scene.Table, _scene.Actions);

        CanvasCall label = canvas.Texts.Last(t => t.Text == "Attack Speed");
        CanvasCall value = canvas.Texts.Last(t => t.Text == "1.25/s");
        float gap = value.TextPos.X - (label.TextPos.X + (label.Text.Length * charWidth));
        Assert.True(gap >= 6f, $"gap {gap} between label and value at char width {charWidth}");
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
    public void Defense_IncludesTheArmorOfTheGear()
    {
        Fill(Status(10, 10, 1f, 1f, 30));

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Defense"));
        Assert.True(_scene.Canvas.HasText("30"));
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
        _scene.View.Sheet = [];

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("No data"));
    }

    [Fact]
    public void ServerStrings_StayLiteralAndShort()
    {
        _scene.View.Sheet =
        [
            new CharacterSheetRow(CharacterSheetRowKind.Attribute, null, "100% %s {0} " + new string('x', 200), "%d %n = %s", false)
        ];

        _scene.Frame();

        Assert.True(_scene.Canvas.Texts.Any(t => t.Text.StartsWith("100% %s {0} ", StringComparison.Ordinal) && t.Text.Length <= 24));
        Assert.True(_scene.Canvas.HasText("%d %n = %s"));
    }

    [Fact]
    public void Header_ShowsClassLevelExperienceAndPoints()
    {
        // Level 2 has earned 5 points and the base stats sit at their start: all 5 are free.
        Fill(Status(10, 10, 1f, 1f, 0, level: 2));

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Class"));
        Assert.True(_scene.Canvas.HasText("Wanderer"));
        Assert.True(_scene.Canvas.HasText("Level"));
        Assert.True(_scene.Canvas.HasText("2"));
        Assert.True(_scene.Canvas.HasText("Experience"));
        Assert.True(_scene.Canvas.HasText("10/100"));
        Assert.True(_scene.Canvas.HasText("Points"));
        Assert.True(_scene.Canvas.HasText("5"));
    }

    private UiRect PlateOf(int attributeIndex)
    {
        // Header rows are 16 u apart from y 38; the first attribute sits 7 u below them, and each
        // group (an attribute plus its derived rows) follows at a fixed pitch.
        float[] y = [109f, 109f + 20f + 16f + 5f, 109f + 20f + 16f + 5f + 20f + 32f + 5f, 109f + 20f + 16f + 5f + 20f + 32f + 5f + 20f + 16f + 5f];
        return _scene.Layout.FromUnits(_scene.Layout.Character, 157f, y[attributeIndex] - 1f, 14f, 14f);
    }

    [Theory]
    [InlineData(0, Dream.Simulation.Stats.StatId.Strength)]
    [InlineData(1, Dream.Simulation.Stats.StatId.Agility)]
    [InlineData(2, Dream.Simulation.Stats.StatId.Vitality)]
    [InlineData(3, Dream.Simulation.Stats.StatId.Energy)]
    public void PlusButton_WithPoints_SpendsOnItsAttribute(int index, Dream.Simulation.Stats.StatId expected)
    {
        Fill(Status(10, 10, 1f, 1f, 0, level: 2));
        _scene.Input.Pointer = InventoryScene.Centre(PlateOf(index));
        _scene.Input.LeftClick = true;

        _scene.Frame();

        // One point, sent as a SpendStat action (PlayerStatSpendIntent).
        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(InventoryHudActionKind.SpendStat, action.Kind);
        Assert.Equal((int)expected, action.SlotIndex);
        Assert.Equal(1, action.Target);
    }

    [Fact]
    public void PlusButton_WithoutPoints_DoesNothing()
    {
        Fill(Status(10, 10, 1f, 1f, 0, level: 1));
        _scene.Input.Pointer = InventoryScene.Centre(PlateOf(0));
        _scene.Input.LeftClick = true;

        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void LongPlayerName_IsCut()
    {
        _scene.View.Inventory = new PlayerInventorySnapshot(new string('N', 300), 4, 4, [], null, null);

        _scene.Frame();

        // Cut to 40 characters, then to the width of the name strip (the text is cut, not clipped).
        CanvasCall name = Assert.Single(_scene.Canvas.Texts, t => t.Text.Length > 0 && t.Text.All(ch => ch == 'N') && t.Color == ItemTierStyle.Body);
        Assert.InRange(name.Text.Length, 1, 40);
        Assert.True(_scene.Canvas.MeasureText(name.Text, UiTextStyle.Body) <= _scene.Layout.FromUnits(_scene.Layout.Character, 35f, 20f, 119f, 8f).W);
    }

    [Fact]
    public void NoSnapshot_DrawsNoName()
    {
        _scene.View.Inventory = null;

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Character"));
        Assert.False(_scene.Canvas.HasText("Tester"));
    }

    private static HudStatusSnapshot PointsStatus(int level, int strength = Dream.Simulation.Players.PlayerStatRules.NewCharacterAttribute, float speed = 2f, int vitality = Dream.Simulation.Players.PlayerStatRules.NewCharacterAttribute) =>
        Status(strength, strength, speed, speed, 0) with { Level = level, BaseVitality = vitality, Vitality = vitality };

    private void ClickPlate(int index)
    {
        _scene.Input.Pointer = InventoryScene.Centre(PlateOf(index));
        _scene.Input.LeftClick = true;
        _scene.Frame();
    }

    [Fact]
    public void WithNoFreePoints_ThePointsReadZero_AndNoPlateSpends()
    {
        Fill(PointsStatus(level: 1));

        ClickPlate(0);

        Assert.True(_scene.Canvas.HasText("Points"));
        Assert.True(_scene.Canvas.HasText("0"));
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void WithFreePoints_TheHeaderShowsThem()
    {
        Fill(PointsStatus(level: 3));

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Points"));
        Assert.True(_scene.Canvas.HasText("10"));
    }

    [Fact]
    public void ACappedAttribute_DoesNotSpend_TheOthersDo_AndAnOverBudgetCharacterSpendsNothing()
    {
        // Strength at the 10000 ceiling is 9000 above the 1000 origin, so the budget has to cover that before any
        // other attribute can spend: level 2000 earns 5 * 1999 = 9995, leaving 995. (Level 400 earned only 1995.)
        Fill(PointsStatus(level: 2000, strength: Dream.Simulation.Players.PlayerCombatStats.MaxStrength));
        ClickPlate(0);
        Assert.Empty(_scene.Actions.Picked);
        ClickPlate(1);
        Assert.Equal((int)Dream.Simulation.Stats.StatId.Agility, Assert.Single(_scene.Actions.Picked).SlotIndex);

        _scene.Actions.Picked.Clear();
        // Over budget from the origin: 30 + 20 + 20 steps (2.0 -> 3.0) = 70 spent against 5 earned at level 2.
        Fill(PointsStatus(level: 2, strength: Dream.Simulation.Players.PlayerStatRules.NewCharacterAttribute + 30, speed: 3f, vitality: Dream.Simulation.Players.PlayerStatRules.NewCharacterAttribute + 20));
        ClickPlate(2);
        Assert.Empty(_scene.Actions.Picked);
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
