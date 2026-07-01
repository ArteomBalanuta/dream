using BulletSharp;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;


public sealed record WorldSessionPresentationState(
    Camera Camera,
    GeometryRenderExport VisibleWorld,
    IReadOnlyList<EnemyActor> ActiveEnemies,
    IReadOnlyList<RigidBody> MapCollisionBodies,
    RigidBody? PlayerCollisionBody,
    HudStatusSnapshot HudStatus)
{
    public static WorldSessionPresentationState Create(
        Camera camera,
        GeometryRenderExport visibleWorld,
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
            visibleWorld,
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
