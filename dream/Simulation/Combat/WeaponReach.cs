using Dream.Simulation.Content;
using Dream.Simulation.Enemies;

namespace Dream.Simulation.Combat;

// How far a primary attack reaches, measured horizontally from the attacker's body centre to the target's
// combat anchor (the same centre-to-centre measure as an enemy's attack range and stand-off). The reach comes
// from the main-hand item's authored "attackRange"; an empty hand or an item without one is unarmed.
public static class WeaponReach
{
    public const float UnarmedMetres = 1.4f;

    // The shortest authored reach: just beyond the wolf's stand-off, so a weapon never reaches less than a
    // wolf that is chasing the wielder can bite from.
    public const float MinMetres = WolfDefinition.PreferredStandOffDistance + 0.1f;

    public const float MaxMetres = 20f;

    // Click-to-attack walks to this fraction of the reach before swinging, so the swing lands with margin.
    public const float ClientApproachFraction = 0.9f;

    // Slack the server grants over the reach for the position drift between the client's and the server's view.
    public const float ServerToleranceMetres = 0.25f;

    // Allocation-free: one dictionary lookup in the shipped catalog.
    public static float ForItemKind(string? itemKind)
        => itemKind != null && ContentCatalog.Shipped.TryGetItem(itemKind, out ItemDefinition definition)
            ? ForItem(definition)
            : UnarmedMetres;

    public static float ForItem(ItemDefinition? item)
        => item?.AttackRange ?? UnarmedMetres;
}
