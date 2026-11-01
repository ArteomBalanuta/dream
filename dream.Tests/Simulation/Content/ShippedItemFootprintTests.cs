using System.Text.Json;
using Xunit;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Tests.Integration;

namespace Dream.Tests.Simulation.Content;

// Classic HUD iteration 2 (D4): every shipped item sets its footprint in items.json, and it is the size the
// item's icon art is drawn at (the art's meta.footprint), so the bag shows what the server places. Also the
// nine new kinds and the loot tables that carry them.
public sealed class ShippedItemFootprintTests
{
    private static readonly string ItemsJson = File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName));

    private static string ArtPath => Path.Combine(RepoRoot.Find(), "dream", "ui", "items", "items.json");

    // item.<key> -> the footprint the art says, for the sprites that exist.
    private static Dictionary<string, (int Cols, int Rows)> ArtFootprints()
    {
        using JsonDocument art = JsonDocument.Parse(File.ReadAllText(ArtPath));
        Dictionary<string, (int, int)> footprints = new(StringComparer.Ordinal);
        foreach (JsonProperty sprite in art.RootElement.GetProperty("sprites").EnumerateObject())
        {
            if (sprite.Value.TryGetProperty("meta", out JsonElement meta) && meta.TryGetProperty("footprint", out JsonElement footprint))
            {
                footprints[sprite.Name] = (footprint[0].GetInt32(), footprint[1].GetInt32());
            }
        }

        return footprints;
    }

    [Fact]
    public void EveryShippedItem_SetsItsFootprintInTheFile_NotJustTheDefault()
    {
        using JsonDocument document = JsonDocument.Parse(ItemsJson);
        JsonElement[] items = [.. document.RootElement.GetProperty("items").EnumerateArray()];

        Assert.Equal(ContentCatalog.Shipped.Items.Count, items.Length);
        foreach (JsonElement item in items)
        {
            string key = item.GetProperty("key").GetString()!;
            Assert.True(item.TryGetProperty("footprint", out JsonElement footprint), $"{key} sets no footprint");
            Assert.Equal(2, footprint.GetArrayLength());
        }
    }

    [Fact]
    public void EveryShippedFootprint_MatchesTheArt_ForEveryItemWithArt()
    {
        Dictionary<string, (int Cols, int Rows)> art = ArtFootprints();
        int compared = 0;
        foreach (ItemDefinition item in ContentCatalog.Shipped.Items)
        {
            if (!art.TryGetValue($"item.{item.Key}", out (int Cols, int Rows) drawn))
            {
                continue;
            }

            Assert.True(
                (item.Footprint.Cols, item.Footprint.Rows) == drawn,
                $"{item.Key}: items.json says {item.Footprint.Cols}x{item.Footprint.Rows}, the art says {drawn.Cols}x{drawn.Rows}");
            compared++;
        }

        // The nine kinds the iteration-1 art was drawn for, at least; the new ones join once their sprites are renamed.
        Assert.True(compared >= 9, $"only {compared} items have art");
    }

    // The art for the nine new kinds is the other half of the same rule: until it is renamed to item.<key> (the
    // art pipeline's job), the item has no icon, and its footprint cannot be checked against anything.
    [Fact]
    public void EveryShippedItem_HasItsIconArt_UnderItsKey()
    {
        Dictionary<string, (int Cols, int Rows)> art = ArtFootprints();

        string[] missing = [.. ContentCatalog.Shipped.Items.Select(item => $"item.{item.Key}").Where(name => !art.ContainsKey(name))];

        Assert.True(missing.Length == 0, "no icon art for: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("gear.iron-gauntlets", "Hands", 2, 2)]
    [InlineData("gear.iron-boots", "Feet", 2, 2)]
    [InlineData("gear.plated-leggings", "Legs", 2, 2)]
    [InlineData("gear.ward-pendant", "Neck", 1, 1)]
    [InlineData("gear.iron-band", "RingLeft,RingRight", 1, 1)]
    [InlineData("gear.travel-cloak", "Back", 3, 2)]
    [InlineData("gear.gossamer-wings", "Back", 3, 2)]
    [InlineData("gear.ember-wisp", "Companion", 2, 2)]
    [InlineData("gear.hunting-spear", "MainHand", 2, 4)]
    public void TheNewKinds_HaveTheirSlotsAndFootprints_AndCanBeRolled(string key, string slots, int cols, int rows)
    {
        Assert.True(ContentCatalog.Shipped.TryGetItem(key, out ItemDefinition? item));

        Assert.Equal(slots, string.Join(",", item.EquipSlots));
        Assert.Equal(new ItemFootprint((byte)cols, (byte)rows), item.Footprint);
        Assert.NotNull(item.Rolls);
        Assert.Empty(item.Modifiers);
        Assert.Equal(HeldLightKind.None, item.HeldLight);
    }

    [Theory]
    [InlineData("tool.builder-hammer", 2, 3)]
    [InlineData("tool.field-blade", 2, 3)]
    [InlineData("tool.stone-maul", 2, 3)]
    [InlineData("gear.iron-blade", 2, 3)]
    [InlineData("tool.survey-lantern", 1, 2)]
    [InlineData("tool.torch", 1, 2)]
    [InlineData("gear.ward-lantern", 1, 2)]
    [InlineData("gear.leather-cap", 2, 2)]
    [InlineData("gear.leather-vest", 2, 2)]
    public void TheExistingKinds_TookTheirFootprintFromTheArt(string key, int cols, int rows)
    {
        Assert.True(ContentCatalog.Shipped.TryGetItem(key, out ItemDefinition? item));

        Assert.Equal(new ItemFootprint((byte)cols, (byte)rows), item.Footprint);
        Assert.Equal(item.Footprint, BagGrid.FootprintOf(key));
    }

    [Fact]
    public void TheNewKinds_AreInBothLootTables_WithTheSameNothingWeightAndTotalAsBefore()
    {
        string[] shared =
        [
            "gear.leather-cap", "gear.leather-vest", "gear.iron-blade", "gear.ward-lantern", "gear.iron-gauntlets", "gear.iron-boots",
            "gear.plated-leggings", "gear.ward-pendant", "gear.iron-band", "gear.travel-cloak", "gear.ember-wisp",
        ];
        Assert.True(ContentCatalog.Shipped.TryGetLootTable("wolf", out LootTable? wolf));
        Assert.True(ContentCatalog.Shipped.TryGetLootTable("dire-wolf", out LootTable? dire));

        Assert.Equal(shared, wolf.Items.Select(entry => entry.ItemKind));
        // The dire wolf adds the wings and the spear, and lists the wisp after the wings.
        Assert.Equal(
            [.. shared.Take(10), "gear.gossamer-wings", "gear.ember-wisp", "gear.hunting-spear"],
            dire.Items.Select(entry => entry.ItemKind));
        Assert.Equal(100, wolf.NothingWeight + wolf.Items.Sum(entry => entry.Weight));
        Assert.Equal(100, dire.NothingWeight + dire.Items.Sum(entry => entry.Weight));
        Assert.Equal(75, wolf.NothingWeight);
        Assert.Equal(50, dire.NothingWeight);
        Assert.All(wolf.Items.Concat(dire.Items), entry => Assert.True(ContentCatalog.Shipped.TryGetItem(entry.ItemKind, out ItemDefinition? def) && def.Rolls != null));
    }
}
