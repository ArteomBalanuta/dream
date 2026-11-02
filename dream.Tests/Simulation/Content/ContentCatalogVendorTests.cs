using System.IO;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// M3 workstream B: npcs.json, the sell prices of items and the gold of enemies in the content loader.
public sealed class ContentCatalogVendorTests
{
    private static string Shipped(string file) => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, file));

    private static ContentCatalog Load(string npcsJson, string? itemsJson = null, string? enemiesJson = null) =>
        ContentCatalog.Load(
            itemsJson ?? Shipped(ContentCatalog.ItemsFileName),
            enemiesJson ?? Shipped(ContentCatalog.EnemiesFileName),
            Shipped(ContentCatalog.LootFileName),
            Shipped(ContentCatalog.SpawnsFileName),
            npcsJson);

    private static string Npcs(string stock, string key = "v") =>
        "{\"version\":1,\"npcs\":[{\"key\":\"" + key + "\",\"displayName\":\"V\",\"visualKey\":\"npc.vendor\",\"offsetX\":1,\"offsetZ\":2,\"stock\":[" + stock + "]}]}";

    [Fact]
    public void TheShippedVendor_StandsNearTheSpawn_AndSellsOnlyUnrolledItemsAboveTheirSellPrice()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;
        NpcDefinition npc = Assert.Single(catalog.Npcs);
        Assert.Equal("npc.vendor", npc.VisualKey);
        Assert.InRange(Math.Abs(npc.OffsetX) + Math.Abs(npc.OffsetZ), 0.1f, 10f);
        Assert.NotEmpty(npc.Stock);
        foreach (NpcStockEntry entry in npc.Stock)
        {
            Assert.True(catalog.TryGetItem(entry.ItemKind, out ItemDefinition? item));
            Assert.Null(item.Rolls);
            Assert.True(item.SellPrice > 0, $"{entry.ItemKind} cannot be sold back");
            Assert.True(entry.Price > item.SellPrice, $"{entry.ItemKind} would mint gold");
        }
    }

    [Fact]
    public void TheShippedWolves_PayGold()
    {
        Assert.True(ContentCatalog.Shipped.TryGetEnemy("Wolf", out EnemyTemplate? wolf));
        Assert.Equal((1, 5), (wolf.GoldMin, wolf.GoldMax));
        Assert.True(ContentCatalog.Shipped.TryGetEnemy("DireWolf", out EnemyTemplate? dire));
        Assert.Equal((5, 15), (dire.GoldMin, dire.GoldMax));
    }

    [Fact]
    public void ContentWithoutNpcs_HasNone_AndKeepsItsDigest()
    {
        ContentCatalog none = Load("{\"version\":1,\"npcs\":[]}");
        Assert.Empty(none.Npcs);
        ContentCatalog withVendor = Load(Shipped(ContentCatalog.NpcsFileName));
        Assert.NotEqual(none.ContentDigest, withVendor.ContentDigest);
        Assert.Equal(ContentCatalog.Shipped.ContentDigest, withVendor.ContentDigest);
    }

    [Fact]
    public void ADirectoryWithoutNpcsJson_LoadsWithNoVendors()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dream-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in new[] { ContentCatalog.ItemsFileName, ContentCatalog.EnemiesFileName, ContentCatalog.LootFileName })
            {
                File.Copy(Path.Combine(ContentCatalog.ShippedDirectory, file), Path.Combine(directory, file));
            }

            Assert.Empty(ContentCatalog.LoadFromDirectory(directory).Npcs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"item\":\"tool.nope\",\"price\":9}", "missing item")]
    [InlineData("{\"item\":\"gear.iron-blade\",\"price\":99}", "rolled item")]
    [InlineData("{\"item\":\"tool.torch\",\"price\":2}", "must exceed the sell price")]
    [InlineData("{\"item\":\"tool.torch\",\"price\":1}", "must exceed the sell price")]
    [InlineData("{\"item\":\"tool.torch\",\"price\":0}", "outside the range")]
    [InlineData("{\"item\":\"tool.torch\",\"price\":9},{\"item\":\"TOOL.TORCH\",\"price\":9}", "duplicate stock item")]
    [InlineData("{\"item\":\"tool.torch\",\"price\":9,\"extra\":1}", "unknown property")]
    public void ABadStockEntry_IsRejected_WithAMessageThatNamesTheFile(string stock, string problem)
    {
        ContentValidationException error = Assert.Throws<ContentValidationException>(() => Load(Npcs(stock)));
        Assert.Contains("npcs.json", error.Message, StringComparison.Ordinal);
        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateNpcKey_IsRejected()
    {
        string one = "{\"key\":\"v\",\"displayName\":\"V\",\"visualKey\":\"npc.vendor\",\"offsetX\":1,\"offsetZ\":2,\"stock\":[]}";
        Assert.Throws<ContentValidationException>(() => Load("{\"version\":1,\"npcs\":[" + one + "," + one + "]}"));
    }

    [Fact]
    public void ASellPriceOnTheItem_MustFitTheStockedPrice_AndBeNonNegative()
    {
        string items = Shipped(ContentCatalog.ItemsFileName);
        string raised = items.Replace("\"sellPrice\": 2,", "\"sellPrice\": 6,");
        Assert.NotEqual(items, raised);
        Assert.Throws<ContentValidationException>(() => Load(Shipped(ContentCatalog.NpcsFileName), itemsJson: raised));

        string negative = items.Replace("\"sellPrice\": 2,", "\"sellPrice\": -1,");
        Assert.Throws<ContentValidationException>(() => Load("{\"version\":1,\"npcs\":[]}", itemsJson: negative));
    }

    [Fact]
    public void AnItemWithoutASellPrice_CannotBeSold()
    {
        string items = Shipped(ContentCatalog.ItemsFileName).Replace("\"sellPrice\": 2,", string.Empty);
        ContentCatalog catalog = Load("{\"version\":1,\"npcs\":[]}", itemsJson: items);
        Assert.True(catalog.TryGetItem("tool.torch", out ItemDefinition? torch));
        Assert.Equal(0, torch.SellPrice);
        Assert.Equal(0L, VendorRules.SellPrice(torch, ItemRarity.Rare));
    }

    [Fact]
    public void GoldRanges_MustBeOrdered()
    {
        string enemies = Shipped(ContentCatalog.EnemiesFileName);
        string reversed = enemies.Replace("\"goldMin\": 1,", "\"goldMin\": 9,");
        Assert.NotEqual(enemies, reversed);
        ContentValidationException error = Assert.Throws<ContentValidationException>(() => Load("{\"version\":1,\"npcs\":[]}", enemiesJson: reversed));
        Assert.Contains("goldMax", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ItemRarity.Common, 1)]
    [InlineData(ItemRarity.Uncommon, 2)]
    [InlineData(ItemRarity.Rare, 4)]
    public void TheSellPrice_ScalesWithRarity(ItemRarity rarity, int factor)
    {
        Assert.True(ContentCatalog.Shipped.TryGetItem("gear.iron-blade", out ItemDefinition? blade));
        Assert.Equal(blade.SellPrice * factor, VendorRules.SellPrice(blade, rarity));
        Assert.Equal(0L, VendorRules.SellPrice(blade, (ItemRarity)9));
    }
}
