using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Inventory;

// Everything that decides one item's roll. Wave I's loot seeds it with (simulation seed, tick,
// killer session hash, enemy actor id, drop index). It never holds an ObjectId, which is not a
// lockstep input. The offline admin command may use the item id as DropIndex because it is not
// lockstep.
public readonly record struct ItemRollSeed(
    ulong SimulationSeed,
    ulong Tick,
    ulong SourceHash,
    ulong SourceId,
    int DropIndex);

public static class ItemRoller
{
    private const ulong RollSalt = 0x6974656D2D726F6CUL; // "item-rol"
    private const float AttackSpeedStep = 0.05f;

    // Pure. Draws one value per range in file order from a fresh DeterministicRandom, so no shared
    // stream advances. The definition must have a roll table.
    public static ItemRoll Roll(ItemDefinition definition, ItemRarity rarity, in ItemRollSeed seed)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Rolls == null)
        {
            throw new InvalidOperationException($"Item '{definition.Key}' has no roll table.");
        }

        IReadOnlyList<ItemRollRange> ranges = definition.Rolls.For(rarity);
        DeterministicRandom random = new(Seed.Combine(
            Seed.Combine(RollSalt, seed.SimulationSeed, seed.Tick, seed.SourceHash, seed.SourceId),
            Seed.FromInt(seed.DropIndex)));
        StatModifier[] modifiers = new StatModifier[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            ItemRollRange range = ranges[i];
            float value;
            if (range.Stat == StatId.AttackSpeed)
            {
                int steps = (int)MathF.Round((range.Max - range.Min) / AttackSpeedStep);
                int k = random.NextInt(0, steps + 1);
                value = Math.Clamp(range.Min + (k * AttackSpeedStep), range.Min, range.Max);
            }
            else
            {
                value = random.NextInt((int)range.Min, (int)range.Max + 1);
            }

            modifiers[i] = new StatModifier(range.Stat, StatOp.Flat, value);
        }

        return new ItemRoll(rarity, modifiers);
    }
}
