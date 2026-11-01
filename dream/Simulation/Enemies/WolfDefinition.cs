namespace Dream.Simulation.Enemies;

// Gameplay dimensions of the wolf enemy: combat anchor, stand-off distance, collision capsule
// and player separation. The authoritative simulation and client replication read these;
// WolfAssetDefinition re-exposes them next to the wolf's model and animation data.
public static class WolfDefinition
{
    public const float CombatAnchorHeight = 0.75f;
    // Between PlayerSeparationDistance (0.95 m) and 90% of the shipped bite range (1.35 m -> 1.215 m), so a
    // chasing wolf stops where it can bite and does not push the player. EnemyActor clamps it to 90% of a
    // template's range (the margin there is only 0.015 m for the shipped wolves; a step is 0.044 m, so a chasing
    // wolf stops on the first tick inside 1.35 m, not at 1.20 m). The content catalog rejects a range that would
    // clamp it below PlayerSeparationDistance.
    public const float PreferredStandOffDistance = 1.20f;
    public const float CollisionRadius = 0.30f;
    public const float CollisionHeight = 0.60f;
    public const float PlayerSeparationDistance = 0.95f;
}
