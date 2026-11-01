namespace Dream.Simulation.Content;

// One weighted item of a loot table. ItemKind references items.json (the item must have a roll table).
public readonly record struct LootEntry(string ItemKind, int Weight);

// A loot table as authored in loot.json. An enemy template references it by Key. Each of the Drops
// rolls picks nothing (NothingWeight) or one of Items by weight, then a rarity by the three rarity
// weights. Weights are validated at load: each 0..1,000,000, the sums positive and inside an int.
public sealed record LootTable(
    string Key,
    int Drops,
    int NothingWeight,
    IReadOnlyList<LootEntry> Items,
    int CommonWeight,
    int UncommonWeight,
    int RareWeight);
