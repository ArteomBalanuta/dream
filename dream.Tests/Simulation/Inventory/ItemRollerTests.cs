using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Xunit;

namespace Dream.Tests.Simulation.Inventory;

// Wave H C0: ItemRoller is pure, draws from its own stream, and stays inside the authored ranges.
public sealed class ItemRollerTests
{
    private static ItemDefinition Gear(string key) =>
        ContentCatalog.Shipped.TryGetItem(key, out ItemDefinition item) ? item : throw new InvalidOperationException(key);

    private static ItemRollSeed Seed(ulong n, int drop = 0) => new(20261029UL + n, 100UL + n, 0xABCDEFUL * (n + 1), 7UL + n, drop);

    [Fact]
    public void TheSameSeed_GivesTheSameRoll()
    {
        ItemDefinition blade = Gear("gear.iron-blade");
        ItemRoll a = ItemRoller.Roll(blade, ItemRarity.Rare, Seed(1));
        ItemRoll b = ItemRoller.Roll(blade, ItemRarity.Rare, Seed(1));

        Assert.Equal(a.Rarity, b.Rarity);
        Assert.Equal(a.Modifiers.Count, b.Modifiers.Count);
        for (int i = 0; i < a.Modifiers.Count; i++)
        {
            Assert.Equal(a.Modifiers[i], b.Modifiers[i]);
        }
    }

    [Fact]
    public void ADifferentDropIndex_GivesADifferentRoll()
    {
        ItemDefinition blade = Gear("gear.iron-blade");
        HashSet<string> distinct = [];
        for (int drop = 0; drop < 32; drop++)
        {
            distinct.Add(ItemRollCodec.Serialize(ItemRoller.Roll(blade, ItemRarity.Rare, Seed(3, drop))));
        }

        // Rare blades have 3 x 11 outcomes; 32 drops that all collapse to one value would mean the index is ignored.
        Assert.True(distinct.Count > 8, $"only {distinct.Count} distinct rolls");
        Assert.NotEqual(
            ItemRollCodec.Serialize(ItemRoller.Roll(blade, ItemRarity.Rare, Seed(3, 0))),
            ItemRollCodec.Serialize(ItemRoller.Roll(blade, ItemRarity.Rare, Seed(3, 1))));
    }

    [Theory]
    [InlineData("gear.iron-blade")]
    [InlineData("gear.ward-lantern")]
    [InlineData("gear.leather-cap")]
    [InlineData("gear.leather-vest")]
    public void EveryValue_IsInsideItsRange_AcrossTenThousandSeeds(string key)
    {
        ItemDefinition item = Gear(key);
        foreach (ItemRarity rarity in Enum.GetValues<ItemRarity>())
        {
            IReadOnlyList<ItemRollRange> ranges = item.Rolls!.For(rarity);
            for (int n = 0; n < 10_000; n++)
            {
                ItemRoll roll = ItemRoller.Roll(item, rarity, Seed((ulong)n, n % 5));

                Assert.Equal(rarity, roll.Rarity);
                Assert.Equal(ranges.Count, roll.Modifiers.Count);
                for (int i = 0; i < ranges.Count; i++)
                {
                    StatModifier modifier = roll.Modifiers[i];
                    Assert.Equal(ranges[i].Stat, modifier.Stat);
                    Assert.Equal(StatOp.Flat, modifier.Op);
                    Assert.InRange(modifier.Value, ranges[i].Min, ranges[i].Max);
                    if (modifier.Stat != StatId.AttackSpeed)
                    {
                        Assert.Equal(MathF.Floor(modifier.Value), modifier.Value);
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryOutcomeOfASmallRange_IsReached()
    {
        ItemDefinition cap = Gear("gear.leather-cap");
        HashSet<(float Armor, float Vitality)> seen = [];
        for (int n = 0; n < 2_000; n++)
        {
            ItemRoll roll = ItemRoller.Roll(cap, ItemRarity.Common, Seed((ulong)n));
            seen.Add((roll.Modifiers[0].Value, roll.Modifiers[1].Value));
        }

        // Armor 1-3 and Vitality 0-1: 3 x 2 outcomes.
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void AttackSpeed_IsOnTheFiveHundredthsGrid_AndBothEndsAreReached()
    {
        ItemDefinition blade = Gear("gear.iron-blade");
        HashSet<float> seen = [];
        for (int n = 0; n < 5_000; n++)
        {
            float value = ItemRoller.Roll(blade, ItemRarity.Uncommon, Seed((ulong)n)).Modifiers[1].Value;
            float steps = (value - 1.25f) / 0.05f;
            Assert.True(MathF.Abs(steps - MathF.Round(steps)) < 1e-3f, $"{value} is off the grid");
            seen.Add(value);
        }

        Assert.Equal(1.25f, seen.Min());
        Assert.Equal(1.75f, seen.Max());
        Assert.Equal(11, seen.Count);
    }

    [Fact]
    public void GoldenRolls_AreFixedForFixedSeeds()
    {
        ItemRollSeed seed = new(20261029UL, 4500UL, 0x1122334455667788UL, 42UL, 3);

        Assert.Equal(
            """{"v":1,"r":"Common","m":[["Strength","Flat",5],["AttackSpeed","Flat",1.3]]}""",
            ItemRollCodec.Serialize(ItemRoller.Roll(Gear("gear.iron-blade"), ItemRarity.Common, seed)));
        Assert.Equal(
            """{"v":1,"r":"Uncommon","m":[["Armor","Flat",2]]}""",
            ItemRollCodec.Serialize(ItemRoller.Roll(Gear("gear.ward-lantern"), ItemRarity.Uncommon, seed)));
        Assert.Equal(
            """{"v":1,"r":"Rare","m":[["Armor","Flat",8],["Vitality","Flat",2]]}""",
            ItemRollCodec.Serialize(ItemRoller.Roll(Gear("gear.leather-cap"), ItemRarity.Rare, seed)));
        Assert.Equal(
            """{"v":1,"r":"Rare","m":[["Armor","Flat",8],["Vitality","Flat",4]]}""",
            ItemRollCodec.Serialize(ItemRoller.Roll(Gear("gear.leather-vest"), ItemRarity.Rare, seed)));
    }

    [Fact]
    public void AnItemWithoutARollTable_CannotBeRolled()
    {
        Assert.Throws<InvalidOperationException>(() => ItemRoller.Roll(Gear("tool.torch"), ItemRarity.Common, Seed(1)));
    }

    [Fact]
    public void EachFieldOfTheSeed_ChangesTheRoll()
    {
        ItemDefinition vest = Gear("gear.leather-vest");
        string Roll(ItemRollSeed seed) => ItemRollCodec.Serialize(ItemRoller.Roll(vest, ItemRarity.Rare, seed));
        ItemRollSeed baseSeed = new(1UL, 2UL, 3UL, 4UL, 5);
        HashSet<string> rolls =
        [
            Roll(baseSeed),
            Roll(baseSeed with { SimulationSeed = 11UL }),
            Roll(baseSeed with { Tick = 12UL }),
            Roll(baseSeed with { SourceHash = 13UL }),
            Roll(baseSeed with { SourceId = 14UL }),
            Roll(baseSeed with { DropIndex = 15 }),
            Roll(baseSeed with { SimulationSeed = 21UL }),
            Roll(baseSeed with { Tick = 22UL }),
            Roll(baseSeed with { SourceHash = 23UL }),
            Roll(baseSeed with { SourceId = 24UL }),
            Roll(baseSeed with { DropIndex = 25 }),
        ];

        // The vest has 5 x 3 outcomes; eleven independent draws that all collide on <= 2 values would mean a field is ignored.
        Assert.True(rolls.Count >= 4, $"only {rolls.Count} distinct rolls");
    }
}
