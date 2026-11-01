using Dream.Simulation.Content;
using Dream.Simulation.Determinism;

namespace Dream.Simulation.Inventory;

// What one loot roll dropped.
public readonly record struct LootDrop(string ItemKind, ItemRarity Rarity);

public static class LootRoller
{
    private const ulong LootSalt = 0x6C6F6F742D726F6CUL; // "loot-rol"; ItemRoller keeps "item-rol"

    // Pure. A fresh DeterministicRandom per drop from the seed and the salt, so no shared stream
    // advances and the item pick and the item's own roll (ItemRoller) never share a stream. Draws the
    // item (or nothing) first, then the rarity. False means nothing dropped. The rarity is drawn only
    // after an item, so a roll that drops nothing consumes one value.
    public static bool TryRoll(LootTable table, in ItemRollSeed seed, out LootDrop drop)
    {
        ArgumentNullException.ThrowIfNull(table);
        DeterministicRandom random = new(Seed.Combine(
            Seed.Combine(LootSalt, seed.SimulationSeed, seed.Tick, seed.SourceHash, seed.SourceId),
            Seed.FromInt(seed.DropIndex)));

        IReadOnlyList<LootEntry> items = table.Items;
        long itemTotal = 0;
        for (int i = 0; i < items.Count; i++)
        {
            itemTotal += items[i].Weight;
        }

        long total = table.NothingWeight + itemTotal;
        if (total <= 0 || itemTotal <= 0 || total > int.MaxValue)
        {
            drop = default;
            return false;
        }

        int pick = random.NextInt(0, (int)total);
        if (pick < table.NothingWeight)
        {
            drop = default;
            return false;
        }

        pick -= table.NothingWeight;
        string? itemKind = null;
        for (int i = 0; i < items.Count; i++)
        {
            int weight = items[i].Weight;
            if (pick < weight)
            {
                itemKind = items[i].ItemKind;
                break;
            }

            pick -= weight;
        }

        long rarityTotal = (long)table.CommonWeight + table.UncommonWeight + table.RareWeight;
        if (itemKind is null || rarityTotal <= 0 || rarityTotal > int.MaxValue)
        {
            drop = default;
            return false;
        }

        int rarityPick = random.NextInt(0, (int)rarityTotal);
        ItemRarity rarity;
        if (rarityPick < table.CommonWeight)
        {
            rarity = ItemRarity.Common;
        }
        else if (rarityPick < table.CommonWeight + table.UncommonWeight)
        {
            rarity = ItemRarity.Uncommon;
        }
        else
        {
            rarity = ItemRarity.Rare;
        }

        drop = new LootDrop(itemKind, rarity);
        return true;
    }
}
