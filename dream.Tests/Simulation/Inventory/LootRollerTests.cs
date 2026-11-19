using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.Inventory;

// Wave I C0: LootRoller is pure, draws from its own stream, and follows the authored weights.
public sealed class LootRollerTests(ITestOutputHelper output)
{
    private static LootTable Table(string key) =>
        ContentCatalog.Shipped.TryGetLootTable(key, out LootTable table) ? table : throw new InvalidOperationException(key);

    private static ItemRollSeed Seed(ulong n, int drop = 0) => new(20261101UL, 1000UL + n, 0xABCDEFUL, 7UL + n, drop);

    private static string Describe(LootTable table, ItemRollSeed seed) =>
        LootRoller.TryRoll(table, seed, out LootDrop drop) ? $"{drop.ItemKind}/{drop.Rarity}" : "nothing";

    [Fact]
    public void TheSameSeed_GivesTheSameDrop()
    {
        LootTable dire = Table("dire-wolf");

        for (ulong n = 0; n < 200; n++)
        {
            Assert.Equal(Describe(dire, Seed(n)), Describe(dire, Seed(n)));
        }
    }

    [Fact]
    public void ADifferentDropIndex_GivesADifferentDrop()
    {
        LootTable dire = Table("dire-wolf");
        int differing = 0;
        for (ulong n = 0; n < 200; n++)
        {
            if (Describe(dire, Seed(n, 0)) != Describe(dire, Seed(n, 1)))
            {
                differing++;
            }
        }

        // 50% nothing and 4 items x 3 rarities: two independent draws agree rarely.
        Assert.True(differing > 100, $"only {differing} of 200 seeds differed between drop 0 and drop 1");
    }

    [Fact]
    public void Weights_HoldOverOneHundredThousandSeeds_WithinOnePercent()
    {
        LootTable dire = Table("dire-wolf");
        const int Seeds = 100_000;
        Dictionary<string, int> items = [];
        Dictionary<ItemRarity, int> rarities = [];
        int nothing = 0;
        for (ulong n = 0; n < Seeds; n++)
        {
            ItemRollSeed seed = new(20261101UL, n, 0x1234UL, 99UL, 0);
            if (!LootRoller.TryRoll(dire, seed, out LootDrop drop))
            {
                nothing++;
                continue;
            }

            items[drop.ItemKind] = items.GetValueOrDefault(drop.ItemKind) + 1;
            rarities[drop.Rarity] = rarities.GetValueOrDefault(drop.Rarity) + 1;
        }

        int itemTotal = dire.Items.Sum(entry => entry.Weight);
        int total = dire.NothingWeight + itemTotal;
        Assert.Equal(dire.NothingWeight / (double)total, nothing / (double)Seeds, 2);
        foreach (LootEntry entry in dire.Items)
        {
            Assert.True(
                Math.Abs((entry.Weight / (double)total) - (items.GetValueOrDefault(entry.ItemKind) / (double)Seeds)) < 0.005,
                $"{entry.ItemKind} drifted from its weight");
        }

        int drops = Seeds - nothing;
        int rarityTotal = dire.CommonWeight + dire.UncommonWeight + dire.RareWeight;
        Assert.Equal(dire.CommonWeight / (double)rarityTotal, rarities.GetValueOrDefault(ItemRarity.Common) / (double)drops, 2);
        Assert.Equal(dire.UncommonWeight / (double)rarityTotal, rarities.GetValueOrDefault(ItemRarity.Uncommon) / (double)drops, 2);
        Assert.Equal(dire.RareWeight / (double)rarityTotal, rarities.GetValueOrDefault(ItemRarity.Rare) / (double)drops, 2);
    }

    [Fact]
    public void ANothingWeightOfZero_AlwaysDrops()
    {
        LootTable table = Table("wolf") with { NothingWeight = 0 };

        for (ulong n = 0; n < 5000; n++)
        {
            Assert.True(LootRoller.TryRoll(table, Seed(n), out LootDrop drop));
            Assert.Contains(table.Items, entry => entry.ItemKind == drop.ItemKind);
        }
    }

    [Fact]
    public void AZeroWeightItem_NeverDrops_AndAZeroRarityNeverRolls()
    {
        LootTable table = new(
            "t", 1, 0,
            [new LootEntry("gear.iron-blade", 0), new LootEntry("gear.leather-cap", 3)],
            CommonWeight: 0, UncommonWeight: 1, RareWeight: 0);

        for (ulong n = 0; n < 2000; n++)
        {
            Assert.True(LootRoller.TryRoll(table, Seed(n), out LootDrop drop));
            Assert.Equal("gear.leather-cap", drop.ItemKind);
            Assert.Equal(ItemRarity.Uncommon, drop.Rarity);
        }
    }

    [Fact]
    public void ADegenerateTable_DropsNothing_InsteadOfThrowing()
    {
        Assert.False(LootRoller.TryRoll(new LootTable("t", 1, 5, [], 1, 1, 1), Seed(1), out LootDrop none));
        Assert.Equal(default, none);
        Assert.False(LootRoller.TryRoll(new LootTable("t", 1, 0, [new LootEntry("gear.iron-blade", 1)], 0, 0, 0), Seed(1), out _));
    }

    [Fact]
    public void GoldenPicks_AreStable()
    {
        LootTable dire = Table("dire-wolf");
        string picks = string.Join(" ", Enumerable.Range(0, 10).Select(n => Describe(dire, new ItemRollSeed(20261101UL, 500UL + (ulong)n, 0xFEEDUL, 42UL, n % 2))));

        output.WriteLine(picks);
        // The item catalogue added kinds to the table (same nothing weight, bigger total), so the picks moved;
        // the royal sorcerer regalia moved them again for the same reason. Rolls are still pure functions of the seed.
        Assert.Equal("nothing consumable.minor-mana/Uncommon gear.ash-staff/Common gear.ember-wisp/Common consumable.minor-heal/Uncommon gear.plate-gauntlets/Rare gear.plated-leggings/Common consumable.minor-heal/Uncommon gear.iron-boots/Uncommon gear.leather-vest/Common", picks);
    }
}
