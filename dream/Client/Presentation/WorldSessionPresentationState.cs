using BulletSharp;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Client.Presentation;

// One frame's presentation capture. ActiveEnemies may be the enemy registry's live list (see
// SelectActiveEnemies), so consumers read it during the frame it was captured for and never keep
// it, or this state, across frames.
public sealed record WorldSessionPresentationState(
    Camera Camera,
    GeometryRenderExport VisibleWorld,
    IReadOnlyList<EnemyActor> ActiveEnemies,
    IReadOnlyList<RigidBody> MapCollisionBodies,
    RigidBody? PlayerCollisionBody,
    HudStatusSnapshot HudStatus,
    FramePerformanceSnapshot Performance)
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
        PlayerCombatStats playerStats,
        int level,
        int experience,
        int experienceToNextLevel,
        FramePerformanceSnapshot performance = default,
        PlayerCombatStats? baseStats = null,
        int armor = 0,
        bool isDead = false,
        int respawnSeconds = 0)
    {
        return new WorldSessionPresentationState(
            camera,
            visibleWorld,
            SelectActiveEnemies(activeEnemies),
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
                playerStats.Vitality,
                level,
                experience,
                experienceToNextLevel,
                // Without a base block the effective stats stand in, as before gear existed.
                baseStats?.Strength ?? playerStats.Strength,
                baseStats?.AttackSpeed ?? playerStats.AttackSpeed,
                baseStats?.Vitality ?? playerStats.Vitality,
                armor,
                isDead,
                respawnSeconds,
                playerStats.Agility,
                playerStats.Energy,
                baseStats?.Agility ?? playerStats.Agility,
                baseStats?.Energy ?? playerStats.Energy,
                baseStats?.StatPoints ?? 0,
                // Mana comes from ServerPlayerVitals (ClassicHudSurface.WithMana), not from the stats.
                PlayerAttributeRules.Defense(playerStats.Agility),
                PlayerAttributeRules.MagicMinDamage(playerStats.Energy),
                PlayerAttributeRules.MagicMaxDamage(playerStats.Energy)),
            performance);
    }

    // When every actor is active, which is the steady state, the caller's list is returned as is
    // instead of being copied each frame. Otherwise only the active actors are copied out.
    private static IReadOnlyList<EnemyActor> SelectActiveEnemies(IEnumerable<EnemyActor> actors)
    {
        if (actors is not IReadOnlyList<EnemyActor> list)
        {
            return actors.Where(actor => actor.IsActive).ToArray();
        }

        int activeCount = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].IsActive)
            {
                activeCount++;
            }
        }

        if (activeCount == list.Count)
        {
            return list;
        }

        var active = new EnemyActor[activeCount];
        int next = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].IsActive)
            {
                active[next++] = list[i];
            }
        }

        return active;
    }
}
