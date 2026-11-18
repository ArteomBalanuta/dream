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
            new InventorySlotSnapshot(64, Item(2, "axe.twinbit")),
            new InventorySlotSnapshot(99, Item(3, "axe.twinbit")),
            new InventorySlotSnapshot(112, Item(4, "axe.twinbit")),
            new InventorySlotSnapshot(5, Item(5, "axe.twinbit")),
            new InventorySlotSnapshot(5, Item(6, "axe.twinbit"))));

        Assert.Equal(5, builder.View.Backpack[5].Item.ItemInstanceId);
        Assert.Equal(1, builder.View.Backpack.Count(s => s.Item.ItemInstanceId != 0));
        Assert.Equal(5, builder.View.BagCells[5]);
    }

    // The builder takes footprints from the view, so these tests do not depend on the shipped items.
    private static HudView ViewWith(Func<string?, ItemFootprint> footprintOf) => new() { FootprintOf = footprintOf };

    private static HudViewBuilder BuilderWith(Func<string?, ItemFootprint> footprintOf) => new(Sprites(), ViewWith(footprintOf));

    private static ItemFootprint Sized(string? kind) => kind switch
    {
        "big" => new ItemFootprint(2, 3),
        "wide" => new ItemFootprint(3, 2),
        "tall" => new ItemFootprint(1, 4),
        _ => ItemFootprint.One
    };

    [Fact]
    public void NewView_HasAnEmptyBoard_NotACellZeroOwnerEverywhere()
    {
        HudView view = new();

        Assert.Equal(64, view.BagCells.Length);
        Assert.All(view.BagCells, owner => Assert.Equal(-1, owner));
        Assert.Equal(64, view.BagFootprints.Length);
        Assert.Equal(0, view.HeldBackCount);
        Assert.Equal(64, HudView.BackpackSlots);
    }

    [Fact]
    public void Refresh_PlacesAnItemOnItsWholeFootprint_AndMapsEachCoveredCellToItsAnchor()
    {
        HudViewBuilder builder = BuilderWith(Sized);

        builder.Refresh(Status(), Inventory(
            new InventorySlotSnapshot(9, Item(1, "big")),
            new InventorySlotSnapshot(29, Item(2, "wide")),
            new InventorySlotSnapshot(7, Item(3, "tall"))));

        HudView view = builder.View;
        int[] big = [9, 10, 17, 18, 25, 26];
        int[] wide = [29, 30, 31, 37, 38, 39];
        for (int cell = 0; cell < 64; cell++)
        {
            Assert.Equal(big.Contains(cell) ? 9 : wide.Contains(cell) ? 29 : -1, view.BagCells[cell]);
        }

        Assert.Equal(new ItemFootprint(2, 3), view.BagFootprints[9]);
        Assert.Equal(new ItemFootprint(3, 2), view.BagFootprints[29]);
        Assert.Equal(1L, view.Backpack[9].Item.ItemInstanceId);
        Assert.Equal(2L, view.Backpack[29].Item.ItemInstanceId);
        // The tall item (7, 15, 23, 31) overlaps the wide one at cell 31, so it is not placed at all.
        Assert.Equal(0L, view.Backpack[7].Item.ItemInstanceId);
        Assert.Equal(-1, view.BagCells[7]);
    }

    [Fact]
    public void Refresh_HostileOverlap_TheFirstItemInTheSnapshotWins_WhateverItsAnchor()
    {
        HudViewBuilder builder = BuilderWith(Sized);

        // The item at the higher anchor is listed first, so it keeps the shared cells.
        builder.Refresh(Status(), Inventory(
            new InventorySlotSnapshot(18, Item(1, "big")),    // 18, 19, 26, 27, 34, 35
            new InventorySlotSnapshot(9, Item(2, "big")),     // 9, 10, 17, 18, 25, 26: shares 18 and 26
            new InventorySlotSnapshot(8, Item(3, "big"))));   // 8, 9, 16, 17, 24, 25: free

        HudView view = builder.View;
        Assert.Equal(1L, view.Backpack[18].Item.ItemInstanceId);
        Assert.Equal(0L, view.Backpack[9].Item.ItemInstanceId);
        Assert.Equal(3L, view.Backpack[8].Item.ItemInstanceId);
        Assert.Equal(18, view.BagCells[26]);
        Assert.Equal(8, view.BagCells[9]);
        Assert.Equal(12, view.BagCells.Count(owner => owner >= 0));
    }

    [Fact]
    public void Refresh_AnItemThatRunsOffTheBoardIsSkipped()
    {
        HudViewBuilder builder = BuilderWith(Sized);

        builder.Refresh(Status(), Inventory(
            new InventorySlotSnapshot(7, Item(1, "big")),      // 2 wide from column 7
            new InventorySlotSnapshot(60, Item(2, "big")),     // 3 tall from row 7
            new InventorySlotSnapshot(61, Item(3, "wide")),    // 3 wide from column 5
            new InventorySlotSnapshot(46, Item(4, "big"))));   // fits exactly in the corner

        HudView view = builder.View;
        Assert.Equal(1, view.Backpack.Count(s => s.Item.ItemInstanceId != 0));
        Assert.Equal(4L, view.Backpack[46].Item.ItemInstanceId);
        Assert.Equal(6, view.BagCells.Count(owner => owner >= 0));
    }

    [Fact]
    public void Refresh_IgnoresItemsWithNoId_AndCountsHeldBackRowsOnce()
    {
        HudViewBuilder builder = BuilderWith(Sized);

        builder.Refresh(Status(), Inventory(
            new InventorySlotSnapshot(3, Item(0, "axe.twinbit")),
            new InventorySlotSnapshot(200, Item(10, "big")),
            new InventorySlotSnapshot(201, Item(11, "big")),
            new InventorySlotSnapshot(201, Item(12, "big")),
            new InventorySlotSnapshot(215, Item(13, "big")),
            new InventorySlotSnapshot(216, Item(14, "big")),
            new InventorySlotSnapshot(199, Item(15, "big")),
            new InventorySlotSnapshot(205, null),
            new InventorySlotSnapshot(206, Item(0, "big")),
            new InventorySlotSnapshot(-200, Item(16, "big"))));

        Assert.Equal(3, builder.View.HeldBackCount);
        Assert.All(builder.View.BagCells, owner => Assert.Equal(-1, owner));
        Assert.All(builder.View.Backpack, slot => Assert.Equal(0L, slot.Item.ItemInstanceId));
    }

    [Fact]
    public void Refresh_AFreshSnapshotClearsTheOldBoardAndTheHeldBackCount()
    {
        HudViewBuilder builder = BuilderWith(Sized);
        builder.Refresh(Status(), Inventory(
            new InventorySlotSnapshot(9, Item(1, "big")),
            new InventorySlotSnapshot(200, Item(2, "big"))));
        Assert.Equal(1, builder.View.HeldBackCount);
        Assert.Equal(6, builder.View.BagCells.Count(owner => owner >= 0));

        builder.Refresh(Status(), Inventory(new InventorySlotSnapshot(0, Item(3, "tall"))));

        Assert.Equal(0, builder.View.HeldBackCount);
        Assert.Equal(new[] { 0, 8, 16, 24 }, Enumerable.Range(0, 64).Where(c => builder.View.BagCells[c] == 0));
        Assert.Equal(4, builder.View.BagCells.Count(owner => owner >= 0));
        Assert.Equal(0L, builder.View.Backpack[9].Item.ItemInstanceId);

        builder.Refresh(Status(), null);

        Assert.All(builder.View.BagCells, owner => Assert.Equal(-1, owner));
        Assert.Equal(0, builder.View.HeldBackCount);
    }

    [Fact]
    public void Refresh_FootprintsComeFromTheShippedCatalogByDefault_AndAnUnknownKindIsOneByOne()
    {
        HudViewBuilder builder = new(Sprites(), new HudView());

        builder.Refresh(Status(), Inventory(new InventorySlotSnapshot(12, Item(1, "no.such.kind"))));

        Assert.Equal(ItemFootprint.One, builder.View.BagFootprints[12]);
        Assert.Equal(12, builder.View.BagCells[12]);
        Assert.Equal(1, builder.View.BagCells.Count(owner => owner >= 0));
        Assert.Equal(BagGrid.FootprintOf("gear.iron-blade"), new HudView().FootprintOf("gear.iron-blade"));
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

        long bytes = AllocationAssert.AtMost(0, () => builder.Refresh(status, inv));

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
