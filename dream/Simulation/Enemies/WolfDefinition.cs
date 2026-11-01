namespace Dream.Simulation.Enemies;

// Gameplay dimensions of the wolf enemy: combat anchor, stand-off distance, collision capsule
// and player separation. The authoritative simulation and client replication read these;
// WolfAssetDefinition re-exposes them next to the wolf's model and animation data.
public static class WolfDefinition
{
    public const float CombatAnchorHeight = 0.75f;
    // Inside the bite range (1.35 m) with a margin for float rounding, and outside PlayerSeparationDistance,
    // so a chasing wolf stops where it can bite and does not push the player.
    public const float PreferredStandOffDistance = 1.20f;
    public const float CollisionRadius = 0.30f;
    public const float CollisionHeight = 0.60f;
    public const float PlayerSeparationDistance = 0.95f;
}
