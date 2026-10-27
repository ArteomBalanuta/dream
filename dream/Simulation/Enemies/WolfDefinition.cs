namespace Dream.Simulation.Enemies;

// Gameplay dimensions of the wolf enemy: combat anchor, stand-off distance, collision capsule
// and player separation. The authoritative simulation and client replication read these;
// WolfAssetDefinition re-exposes them next to the wolf's model and animation data.
public static class WolfDefinition
{
    public const float CombatAnchorHeight = 0.75f;
    public const float PreferredStandOffDistance = 2.00f;
    public const float CollisionRadius = 0.30f;
    public const float CollisionHeight = 0.60f;
    public const float PlayerSeparationDistance = 0.95f;
}
