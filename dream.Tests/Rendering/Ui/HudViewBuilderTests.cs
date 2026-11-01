using Xunit;
using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Simulation.Inventory;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Ui;

public class HudViewBuilderTests
{
    private static UiSpriteTable Sprites()
    {
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        return table!;
    }

    private static HudStatusSnapshot Status(int health = 80) => new(
        CurrentHealth: health, MaxHealth: 100, InteractionMode: Dream.Simulation.Players.PlayerInteractionMode.Combat,
        MinDamage: 1, MaxDamage: 3, Strength: 10, AttackSpeed: 2f, Vitality: 10, Level: 1, Experience: 5, ExperienceToNextLevel: 100);

    private static InventoryItemSnapshot Item(long id, string? kind, ItemRarity rarity = ItemRarity.Common, string? name = "Thing") => new(
        id, kind!, name!, "visual", 1, "desc", Array.Empty<string>(), Rarity: rarity);

    private static PlayerInventorySnapshot Inventory(params InventorySlotSnapshot[] slots) =>
        new("tester", 4, 4, slots);

    [Fact]
    public void Refresh_MapsKnownKindsToIconsIgnoringCase_AndTiersFromRarity()
    {
        UiSpriteTable sprites = Sprites();
        HudViewBuilder builder = new(sprites, new HudView());
        PlayerInventorySnapshot inv = Inventory(
            new InventorySlotSnapshot(0, Item(1, "axe.twinbit")),
            new InventorySlotSnapshot(1, Item(2, "AXE.TWINBIT", ItemRarity.Uncommon)),
            new InventorySlotSnapshot(2, Item(3, "axe.twinbit", ItemRarity.Rare)));

        builder.Refresh(Status(), inv);

        HudItemView[] bag = builder.View.Backpack;
        Assert.True(bag[0].HasIcon);
        Assert.Equal(sprites.Get("item.axe.twinbit"), bag[0].Icon);
        Assert.True(bag[1].HasIcon);
        Assert.Equal(ItemTier.Common, bag[0].Tier);
        Assert.Equal(ItemTier.Uncommon, bag[1].Tier);
        Assert.Equal(ItemTier.Rare, bag[2].Tier);
    }

    [Fact]
    public void Refresh_UnknownEmptyOrNullKindHasNoIcon_ButStillTheItem()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());
        PlayerInventorySnapshot inv = Inventory(
            new InventorySlotSnapshot(0, Item(1, "no.such.kind")),
            new InventorySlotSnapshot(1, Item(2, "")),
            new InventorySlotSnapshot(2, Item(3, null)));

        builder.Refresh(Status(), inv);

        for (int i = 0; i < 3; i++)
        {
            Assert.False(builder.View.Backpack[i].HasIcon);
            Assert.Equal(i + 1, builder.View.Backpack[i].Item.ItemInstanceId);
        }
    }

    [Fact]
    public void Refresh_UndefinedRarityIsCommon()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());

        builder.Refresh(Status(), Inventory(new InventorySlotSnapshot(0, Item(1, "axe.twinbit", (ItemRarity)99))));

        Assert.Equal(ItemTier.Common, builder.View.Backpack[0].Tier);
    }

    [Fact]
    public void Refresh_HostileSnapshotsDoNotThrow()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());
        PlayerInventorySnapshot[] hostile =
        [
            default,
            Inventory(
                new InventorySlotSnapshot(-1, Item(1, "axe.twinbit")),
                new InventorySlotSnapshot(16, Item(2, "axe.twinbit")),
                new InventorySlotSnapshot(int.MaxValue, Item(3, "axe.twinbit")),
                new InventorySlotSnapshot(int.MinValue, Item(4, "axe.twinbit")),
                new InventorySlotSnapshot(5, Item(5, null, name: null)),
                new InventorySlotSnapshot(5, Item(6, "axe.twinbit")),
                new InventorySlotSnapshot(6, null)),
            new PlayerInventorySnapshot("x", -3, 99, null!, new InventorySlotSnapshot(100, Item(7, "axe.twinbit")), null),
            new PlayerInventorySnapshot(null!, 4, 4, Array.Empty<InventorySlotSnapshot>(),
                null, [new InventorySlotSnapshot(500, Item(8, "axe.twinbit")), new InventorySlotSnapshot(101, Item(9, "axe.twinbit"))]),
        ];

        foreach (PlayerInventorySnapshot snapshot in hostile)
        {
            builder.Refresh(Status(), snapshot);
        }

        builder.Refresh(Status(), null);
        Assert.Equal(HudView.BackpackSlots, builder.View.Backpack.Length);
        Assert.All(builder.View.Backpack, slot => Assert.Equal(0, slot.Item.ItemInstanceId));
    }

    [Fact]
    public void Refresh_OutOfRangeSlotsAreSkipped_AndTheFirstItemInASlotWins()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());

        builder.Refresh(Status(), Inventory(
            new InventorySlotSnapshot(-1, Item(1, "axe.twinbit")),
            new InventorySlotSnapshot(16, Item(2, "axe.twinbit")),
            new InventorySlotSnapshot(5, Item(5, "axe.twinbit")),
            new InventorySlotSnapshot(5, Item(6, "axe.twinbit"))));

        Assert.Equal(5, builder.View.Backpack[5].Item.ItemInstanceId);
        Assert.Equal(1, builder.View.Backpack.Count(s => s.Item.ItemInstanceId != 0));
    }

    [Fact]
    public void Refresh_EquippedAliasKeepsItsFlag_AndWornFollowsTheEquipment()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());
        InventoryItemSnapshot sword = Item(10, "axe.twinbit");
        PlayerInventorySnapshot inv = new(
            "p", 4, 4,
            [new InventorySlotSnapshot(3, sword, IsEquipped: true)],
            new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, sword));

        builder.Refresh(Status(), inv);

        Assert.True(builder.View.Backpack[3].Equipped);
        Assert.Equal(10, builder.View.Worn[0].Item.ItemInstanceId);
        Assert.True(builder.View.Worn[0].Equipped);
        Assert.Equal(0, builder.View.Worn[1].Item.ItemInstanceId);
        Assert.Equal(HudView.WornSlots, builder.View.Worn.Length);
    }

    [Fact]
    public void Refresh_RebuildsOnlyWhenStatusOrInventoryChanged()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());
        PlayerInventorySnapshot inv = Inventory(new InventorySlotSnapshot(0, Item(1, "axe.twinbit")));

        Assert.True(builder.Refresh(Status(), inv));
        CharacterStatRow[] rows = builder.View.StatRows;
        CharacterSlotRow[] equipment = builder.View.Equipment;

        Assert.False(builder.Refresh(Status(), inv));
        Assert.Same(rows, builder.View.StatRows);
        Assert.Same(equipment, builder.View.Equipment);

        Assert.True(builder.Refresh(Status(health: 50), inv));
        Assert.NotSame(rows, builder.View.StatRows);
        // The same contents in new lists are the same inventory: nothing to rebuild, no change.
        Assert.False(builder.Refresh(Status(health: 50), Inventory(new InventorySlotSnapshot(0, Item(1, "axe.twinbit")))));
        Assert.False(builder.InventoryChanged);
        Assert.True(builder.Refresh(Status(health: 50), Inventory(new InventorySlotSnapshot(0, Item(2, "axe.twinbit")))));
        Assert.True(builder.InventoryChanged);
    }

    [Fact]
    public void Refresh_AHealthChange_RebuildsTheStatRowsButNotTheItems_AndAnInventoryChangeTheReverse()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());
        PlayerInventorySnapshot inv = Inventory(new InventorySlotSnapshot(0, Item(1, "axe.twinbit")));
        builder.Refresh(Status(), inv);
        HudItemView[] bagBefore = builder.View.Backpack;
        CharacterSlotRow[] equipmentBefore = builder.View.Equipment;
        Assert.Equal((1, 1), (builder.StatRowRebuilds, builder.ItemRowRebuilds));

        builder.Refresh(Status(health: 50), inv);
        CharacterStatRow[] statsAfterHealth = builder.View.StatRows;

        Assert.Equal((2, 1), (builder.StatRowRebuilds, builder.ItemRowRebuilds));
        Assert.Same(equipmentBefore, builder.View.Equipment);
        Assert.Same(bagBefore, builder.View.Backpack);
        Assert.True(builder.View.Backpack[0].HasIcon);

        builder.Refresh(Status(health: 50), Inventory(new InventorySlotSnapshot(0, Item(2, "axe.twinbit"))));

        Assert.Equal((2, 2), (builder.StatRowRebuilds, builder.ItemRowRebuilds));
        Assert.Same(statsAfterHealth, builder.View.StatRows);
        Assert.Equal(2L, builder.View.Backpack[0].Item.ItemInstanceId);
    }

    [Fact]
    public void Refresh_AnUnchangedFrame_AllocatesNothing()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());
        PlayerInventorySnapshot inv = Inventory(new InventorySlotSnapshot(0, Item(1, "axe.twinbit")));
        HudStatusSnapshot status = Status();
        builder.Refresh(status, inv);

        long bytes = AllocationAssert.MeasureBytesPerIteration(() => builder.Refresh(status, inv));

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Refresh_WithNoInventory_LeavesEmptyRowsAndAnEmptyBackpack()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());

        builder.Refresh(Status(), null);

        Assert.Null(builder.View.Inventory);
        Assert.Equal(EquipmentSlots.Count, builder.View.Equipment.Length);
        Assert.All(builder.View.Backpack, slot => Assert.False(slot.HasIcon));
    }

    [Fact]
    public void Catalog_CoversEveryItemInTheConfig()
    {
        // Same rule the sprite table test pins, here through the catalog the view uses.
        HudItemCatalog catalog = new(Sprites());
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AssetPaths.Config("content/items.json")));
        int checkedKinds = 0;
        foreach (System.Text.Json.JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.True(catalog.TryGetIcon(item.GetProperty("key").GetString(), out _));
            checkedKinds++;
        }

        Assert.True(checkedKinds > 0);
    }
}
