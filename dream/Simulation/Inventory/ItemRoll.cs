using Dream.Simulation.Stats;

namespace Dream.Simulation.Inventory;

// The stats an item instance was created with: fixed at creation and never changed afterwards.
// Damaged is what a stored roll that fails to parse becomes: Common, no modifiers, so inert.
public sealed record ItemRoll(ItemRarity Rarity, IReadOnlyList<StatModifier> Modifiers)
{
    public static ItemRoll Damaged { get; } = new(ItemRarity.Common, Array.Empty<StatModifier>());

    public bool IsDamaged => ReferenceEquals(this, Damaged);
}
