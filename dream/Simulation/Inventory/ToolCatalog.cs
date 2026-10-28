namespace Dream.Simulation.Inventory;

public static class ToolCatalog
{
    private static readonly IReadOnlyDictionary<string, ToolDefinition> Definitions =
        new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["tool.builder-hammer"] = new(
                "tool.builder-hammer",
                "Builder Hammer",
                "Reliable close-range construction tool for quick world edits.",
                "held.tool.builder",
                ["Role: Building", "Impact: +2 Strength", "Tempo: +0.25 Attack Speed"],
                new ToolStatModifier(StrengthBonus: 2, AttackSpeedBonus: 0.25f)),
            ["tool.field-blade"] = new(
                "tool.field-blade",
                "Field Blade",
                "Short combat blade with quick recovery and clean follow-up swings.",
                "held.tool.blade",
                ["Role: Combat", "Impact: +4 Strength", "Tempo: +1.50 Attack Speed"],
                new ToolStatModifier(StrengthBonus: 4, AttackSpeedBonus: 1.50f)),
            ["tool.stone-maul"] = new(
                "tool.stone-maul",
                "Stone Maul",
                "Heavy breaker meant for deliberate hits and strong stagger pressure.",
                "held.tool.maul",
                ["Role: Breaker", "Impact: +8 Strength", "Tempo: -0.75 Attack Speed"],
                new ToolStatModifier(StrengthBonus: 8, AttackSpeedBonus: -0.75f)),
            ["tool.survey-lantern"] = new(
                "tool.survey-lantern",
                "Survey Lantern",
                "Utility tool for traversal loadouts and world inspection passes.",
                "held.tool.lantern",
                ["Role: Utility", "Impact: +1 Strength", "Tempo: +0.10 Attack Speed"],
                new ToolStatModifier(StrengthBonus: 1, AttackSpeedBonus: 0.10f),
                HeldLightKind.Lantern),
            // Wave T2: the first item that lights the night. No stat bonus.
            ["tool.torch"] = new(
                "tool.torch",
                "Torch",
                "A burning torch that lights the ground around you after dark.",
                "held.tool.torch",
                ["Role: Light", "Light: warm, radius 8"],
                default,
                HeldLightKind.Torch)
        };

    public static IReadOnlyList<string> StarterItemKinds { get; } =
    [
        "tool.builder-hammer",
        "tool.field-blade",
        "tool.stone-maul",
        "tool.survey-lantern",
        // Last, so the first starter item (the hammer) stays the one equipped. Players whose
        // backpack predates it receive it through PlayerInventoryService's starter top-up.
        "tool.torch"
    ];

    public static ToolDefinition Resolve(string itemKind)
    {
        if (!Definitions.TryGetValue(itemKind, out ToolDefinition definition))
        {
            throw new KeyNotFoundException($"Unknown tool definition '{itemKind}'.");
        }

        return definition;
    }
}
