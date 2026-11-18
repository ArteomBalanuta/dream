using System.Globalization;
using OpenTK.Mathematics;
using Xunit;
using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;

namespace Dream.Tests.Rendering.Ui;

// The character sheet's Attack Range follows the equipped main-hand weapon through the real surface path: the
// frame carries the inventory, ClassicHudSurface.Draw fills the status's reach from it (WithAttackReach), and the
// sheet's row is built from that status. Nothing here sets AttackRange on the status by hand.
public class ClassicHudSurfaceAttackReachTests
{
    private sealed class NullCanvas : IUiCanvas
    {
        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }
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

    private sealed class FakeBackend : IClassicHudBackend
    {
        public ClassicHudBackendState State { get; set; } = ClassicHudBackendState.Ready;
        public string? FailureReason { get; set; }
        public UiSpriteTable? Sprites { get; set; }
        public IUiCanvas BeginFrame(in UiLayout layout) => new NullCanvas();
        public void Submit() { }
        public void Dispose() { }
    }

    private static UiSpriteTable Sprites()
    {
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        return table!;
    }

    private static PlayerInventorySnapshot MainHand(string itemKind)
    {
        Assert.True(ContentCatalog.Shipped.TryGetItem(itemKind, out ItemDefinition definition));
        InventoryItemSnapshot item = new(
            7, definition.Key, definition.DisplayName, definition.VisualId, 1, definition.Description,
            definition.DetailLines, definition.Modifiers.Count == 0 ? null : definition.Modifiers,
            definition.HeldLight, definition.EquipSlotMask);
        return new PlayerInventorySnapshot(
            "p", 4, 4, [], new InventorySlotSnapshot(EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand), item, IsEquipped: true));
    }

    private static ClassicHudFrame Frame(PlayerInventorySnapshot? inventory) => new()
    {
        Width = 1920,
        Height = 1080,
        ConfiguredScale = 0f,
        Pointer = new UiPointer(Vector2.Zero, false, false, false),
        Inventory = inventory
    };

    private static string AttackRangeRow(ClassicHudSurface surface)
    {
        CharacterSheetRow row = Assert.Single(surface.View.Sheet, candidate => candidate.Label == "Attack Range");
        return row.Value;
    }

    private static string Metres(float metres) => metres.ToString("0.0#", CultureInfo.InvariantCulture) + " m";

    [Fact]
    public void Draw_FillsTheSheetsAttackRangeFromTheEquippedMainHand_NotFromTheStatus()
    {
        float dagger = WeaponReach.ForItemKind("gear.iron-dagger");
        float bow = WeaponReach.ForItemKind("gear.long-bow");
        Assert.NotEqual(WeaponReach.UnarmedMetres, dagger);
        Assert.NotEqual(dagger, bow);
        ClassicHudSurface surface = new(new FakeBackend { Sprites = Sprites() });

        Assert.True(surface.Draw(Frame(MainHand("gear.iron-dagger"))));
        Assert.Equal(dagger, surface.View.Status.AttackRange);
        Assert.Equal(Metres(dagger), AttackRangeRow(surface));

        // The same surface, next frame: a longer weapon replaces it.
        Assert.True(surface.Draw(Frame(MainHand("gear.long-bow"))));
        Assert.Equal(bow, surface.View.Status.AttackRange);
        Assert.Equal(Metres(bow), AttackRangeRow(surface));

        // Nothing in the main hand (no inventory, then an empty one): the unarmed reach.
        Assert.True(surface.Draw(Frame(null)));
        Assert.Equal(WeaponReach.UnarmedMetres, surface.View.Status.AttackRange);
        Assert.Equal(Metres(WeaponReach.UnarmedMetres), AttackRangeRow(surface));
        Assert.True(surface.Draw(Frame(new PlayerInventorySnapshot("p", 4, 4, []))));
        Assert.Equal(Metres(WeaponReach.UnarmedMetres), AttackRangeRow(surface));
    }

    [Fact]
    public void Draw_OverwritesAnAttackRangeTheCallersStatusCarried()
    {
        ClassicHudSurface surface = new(new FakeBackend { Sprites = Sprites() });
        ClassicHudFrame frame = Frame(MainHand("gear.iron-dagger")) with { Status = new HudStatusSnapshot { AttackRange = 99f } };

        Assert.True(surface.Draw(frame));

        Assert.Equal(WeaponReach.ForItemKind("gear.iron-dagger"), surface.View.Status.AttackRange);
    }
}
