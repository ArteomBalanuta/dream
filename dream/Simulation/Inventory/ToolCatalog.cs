using Dream.Simulation.Content;

namespace Dream.Simulation.Inventory;

// A thin facade over ContentCatalog.Shipped (config/content/items.json). Tick and snapshot paths
// use TryResolve: an item_kind the catalog no longer knows (a retired key) is an inert "Unknown
// item", never an exception. Resolve throws and is for callers that just took the kind from the
// catalog itself (the starter kit).
public static class ToolCatalog
{
    // The description of an item whose kind is not in the catalog. Its row is left untouched.
    public const string UnknownItemDescription = "Unknown item";

    private static readonly IReadOnlyDictionary<string, ToolDefinition> Definitions = BuildDefinitions();

    // The starter kit, in seeding order, with the kit version that introduced each kind. A player's
    // inventory records the kit version it has received (player_starter_grants); PlayerInventoryService
    // grants only the kinds introduced after it, so an item a player gets rid of is never granted
    // again. Append new kinds with the next version and raise starterKitVersion in items.json; never
    // change an existing entry's version.
    public static IReadOnlyList<StarterKitEntry> StarterKit => ContentCatalog.Shipped.StarterKit;

    // The newest kit version: the highest IntroducedVersion in StarterKit.
    public static int CurrentStarterKitVersion => ContentCatalog.Shipped.StarterKitVersion;

    // The kit an inventory with items but no grant record is taken to hold: every inventory seeded
    // before kit versions were recorded got at least the pre-T2 kit (v1).
    public static int UnrecordedStarterKitVersion => ContentCatalog.Shipped.UnrecordedStarterKitVersion;

    public static IReadOnlyList<string> StarterItemKinds { get; } = [.. ContentCatalog.Shipped.StarterKit.Select(entry => entry.ItemKind)];

    // False for an unknown kind. Allocates nothing.
    public static bool TryResolve(string itemKind, out ToolDefinition definition)
    {
        if (itemKind != null && Definitions.TryGetValue(itemKind, out definition))
        {
            return true;
        }

        definition = default;
        return false;
    }

    public static ToolDefinition Resolve(string itemKind)
    {
        if (!TryResolve(itemKind, out ToolDefinition definition))
        {
            throw new KeyNotFoundException($"Unknown tool definition '{itemKind}'.");
        }

        return definition;
    }

    private static IReadOnlyDictionary<string, ToolDefinition> BuildDefinitions()
    {
        Dictionary<string, ToolDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);
        foreach (ItemDefinition item in ContentCatalog.Shipped.Items)
        {
            definitions[item.Key] = new ToolDefinition(
                item.Key,
                item.DisplayName,
                item.Description,
                item.VisualId,
                item.DetailLines,
                item.StatModifier,
                item.HeldLight);
        }

        return definitions;
    }
}

// One starter-kit kind and the kit version that introduced it (see ToolCatalog.StarterKit).
public readonly record struct StarterKitEntry(string ItemKind, int IntroducedVersion);
