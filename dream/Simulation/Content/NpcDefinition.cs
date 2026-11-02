namespace Dream.Simulation.Content;

// One item a vendor sells, at its price in gold (npcs.json).
public readonly record struct NpcStockEntry(string ItemKind, long Price);

// A vendor as authored in npcs.json (M3). It stands OffsetX and OffsetZ metres from the world spawn (so a
// stored world with another spawn still has it near the player), on the spawn's floor height. Its actor id
// is ReplicatedActorIdRanges.FirstNpcId plus its index in file order. Stock holds fixed-stat items only,
// each priced above its sell price. Greeting is what a quest giver says when talked to (M6); empty for a
// plain vendor.
public sealed record NpcDefinition(
    string Key,
    string DisplayName,
    string VisualKey,
    float OffsetX,
    float OffsetZ,
    IReadOnlyList<NpcStockEntry> Stock,
    string Greeting = "");
