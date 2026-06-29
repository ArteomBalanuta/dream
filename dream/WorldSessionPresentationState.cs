using BulletSharp;

public sealed record WorldSessionPresentationState(
    Camera Camera,
    IReadOnlyList<Cube> VisibleWorldCubes,
    IReadOnlyList<EnemyActor> ActiveEnemies,
    IReadOnlyList<RigidBody> MapCollisionBodies,
    RigidBody? PlayerCollisionBody,
    HudStatusSnapshot HudStatus)
{
    public static WorldSessionPresentationState Create(
        Camera camera,
        IEnumerable<Cube> visibleWorldCubes,
        IEnumerable<EnemyActor> activeEnemies,
        IReadOnlyList<RigidBody> mapCollisionBodies,
        RigidBody? playerCollisionBody,
        HealthState health,
        PlayerInteractionMode interactionMode,
        PlayerCombatDerivedStats derivedStats,
        PlayerCombatStats playerStats)
    {
        return new WorldSessionPresentationState(
            camera,
            visibleWorldCubes.ToArray(),
            activeEnemies.Where(actor => actor.IsActive).ToArray(),
            mapCollisionBodies,
            playerCollisionBody,
            new HudStatusSnapshot(
                health.CurrentHealth,
                health.MaxHealth,
                interactionMode,
                derivedStats.MinDamage,
                derivedStats.MaxDamage,
                playerStats.Strength,
                playerStats.AttackSpeed,
                playerStats.Vitality));
    }
}
