using Dream.Rendering.Ui;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

// Classic HUD iteration 2, C0a: the contracts the parallel workstreams build against. A canvas that
// knows nothing of the new draw calls keeps compiling and draws the plain sprite; the view carries 12
// worn slots and the gem animation fields; the new sprite names exist but are not required yet.
public sealed class ClassicHudIteration2ContractTests
{
    // Implements only what the interface required before this iteration.
    private sealed class OldCanvas : IUiCanvas
    {
        public List<(UiSprite Sprite, UiRect Dst, UiColor Tint)> Sprites { get; } = [];

        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) => Sprites.Add((s, dst, tint));

        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }

        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Solid(UiRect dst, UiColor color) { }

        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) { }

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => 0f;

        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => 0f;

        public void PushClip(UiRect r) { }

        public void PopClip() { }
    }

    private static readonly UiSprite Sprite = new(UiPage.Hud, 10, 20, 30, 40, 0, 0, 0, 0, 0, 0, UiTile.None, 1, 1);

    [Theory]
    [InlineData(UiGemKind.Health)]
    [InlineData(UiGemKind.Mana)]
    public void Gem_OnACanvasWithoutTheMode_DrawsThePlainSprite(UiGemKind kind)
    {
        OldCanvas old = new();
        IUiCanvas canvas = old;
        UiRect dst = new(5, 6, 50, 50);
        UiColor tint = new(1, 2, 3, 4);

        canvas.Gem(in Sprite, dst, kind, 0.3f, 0.5f, 0.2f, 0.7f, tint);

        (UiSprite sprite, UiRect drawn, UiColor drawnTint) = Assert.Single(old.Sprites);
        Assert.Equal(Sprite, sprite);
        Assert.Equal(dst, drawn);
        Assert.Equal(tint, drawnTint);
    }

    [Fact]
    public void Additive_OnACanvasWithoutTheMode_DrawsThePlainSprite()
    {
        OldCanvas old = new();
        IUiCanvas canvas = old;
        UiRect dst = new(1, 2, 128, 64);

        canvas.Additive(in Sprite, dst, UiColor.White);

        (UiSprite sprite, UiRect drawn, UiColor drawnTint) = Assert.Single(old.Sprites);
        Assert.Equal(Sprite, sprite);
        Assert.Equal(dst, drawn);
        Assert.Equal(UiColor.White, drawnTint);
    }

    [Fact]
    public void TheGemKinds_AreHealthThenMana()
    {
        Assert.Equal([UiGemKind.Health, UiGemKind.Mana], Enum.GetValues<UiGemKind>());
        Assert.Equal(0, (int)UiGemKind.Health);
        Assert.Equal(1, (int)UiGemKind.Mana);
    }

    [Fact]
    public void TheHudView_HasOneWornSlotPerEquipmentSlot_AndOneBagAnchorPerCellOfTheEightByEightBoard()
    {
        HudView view = new();

        Assert.Equal(12, HudView.WornSlots);
        Assert.Equal(EquipmentSlots.Count, HudView.WornSlots);
        Assert.Equal(HudView.WornSlots, view.Worn.Length);

        // The bag is the 8x8 board: Backpack is indexed by anchor cell, BagCells by cell.
        Assert.Equal(64, HudView.BackpackSlots);
        Assert.Equal(BagGrid.CellCount, HudView.BackpackSlots);
        Assert.Equal(HudView.BackpackSlots, view.Backpack.Length);
        Assert.Equal(HudView.BackpackSlots, view.BagCells.Length);
        Assert.Equal(HudView.BackpackSlots, view.BagFootprints.Length);
    }

    [Fact]
    public void TheGemAnimationFields_StartAtZero_AndTakeValues()
    {
        HudView view = new();

        Assert.Equal(0f, view.HealthLevel);
        Assert.Equal(0f, view.ManaLevel);
        Assert.Equal(0f, view.AnimationSeconds);
        Assert.Equal(0f, view.GemPulse);

        view.HealthLevel = 0.75f;
        view.ManaLevel = 1f;
        view.AnimationSeconds = 12.5f;
        view.GemPulse = 0.4f;

        Assert.Equal(0.75f, view.HealthLevel);
        Assert.Equal(1f, view.ManaLevel);
        Assert.Equal(12.5f, view.AnimationSeconds);
        Assert.Equal(0.4f, view.GemPulse);
    }

    [Fact]
    public void TheIterationTwoSpriteNames_AreDefined_ButNotYetRequired()
    {
        string[] added =
        [
            UiSpriteNames.GemMpFull, UiSpriteNames.GemHpMask, UiSpriteNames.GemHpSpec, UiSpriteNames.GemMpMask,
            UiSpriteNames.GemMpSpec, UiSpriteNames.GemSettingLeft, UiSpriteNames.GemSettingRight,
            UiSpriteNames.GemHpSpill, UiSpriteNames.GemMpSpill, UiSpriteNames.HudNumSlash,
            UiSpriteNames.Well1x1, UiSpriteNames.Well3x2,
            UiSpriteNames.SilGloves, UiSpriteNames.SilBoots, UiSpriteNames.SilPants, UiSpriteNames.SilPendant,
            UiSpriteNames.SilRing, UiSpriteNames.SilWings, UiSpriteNames.SilPet,
        ];

        Assert.Equal(
            [
                "gem.mp.full", "gem.hp.mask", "gem.hp.spec", "gem.mp.mask", "gem.mp.spec", "gem.setting.left",
                "gem.setting.right", "gem.hp.spill", "gem.mp.spill", "hudnum.slash", "well.1x1", "well.3x2",
                "sil.gloves", "sil.boots", "sil.pants", "sil.pendant", "sil.ring", "sil.wings", "sil.pet",
            ],
            added);
        Assert.Equal(added.Length, added.Distinct().Count());
        Assert.All(added, name => Assert.DoesNotContain(name, UiSpriteNames.Required));
    }

    [Fact]
    public void TheBarNumerals_AreNamedByDigit_AndNotRequiredYet()
    {
        for (int d = 0; d <= 9; d++)
        {
            Assert.Equal("hudnum." + d, UiSpriteNames.HudNum(d));
            Assert.DoesNotContain(UiSpriteNames.HudNum(d), UiSpriteNames.Required);
        }
    }

    [Fact]
    public void TheShippedArt_AlreadyHasTheSpritesThatExistToday()
    {
        // The names whose art ships already; the rest (setting, spill, numerals) arrive with the art.
        Assert.True(UiSpriteTable.TryLoad(Dream.Assets.AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        foreach (string name in new[]
        {
            UiSpriteNames.GemMpFull, UiSpriteNames.GemHpMask, UiSpriteNames.GemHpSpec, UiSpriteNames.GemMpMask, UiSpriteNames.GemMpSpec,
            UiSpriteNames.Well1x1, UiSpriteNames.Well3x2, UiSpriteNames.SilGloves, UiSpriteNames.SilBoots, UiSpriteNames.SilPants,
            UiSpriteNames.SilPendant, UiSpriteNames.SilRing, UiSpriteNames.SilWings, UiSpriteNames.SilPet,
        })
        {
            Assert.True(table!.TryGet(name, out _), name);
        }
    }
}
