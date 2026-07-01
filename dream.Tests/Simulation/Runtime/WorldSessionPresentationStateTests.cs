using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Runtime;


public class WorldSessionPresentationStateTests
{
    [Fact]
    public void Create_Composes_Hud_Status_From_Authoritative_Runtime_Values()
    {
        PlayerCombatStats playerStats = new()
        {
            Strength = 14,
            AttackSpeed = 2.5f,
            Vitality = 12
        };
        PlayerCombatDerivedStats derivedStats = playerStats.Derive();
        HealthState health = new(derivedStats.MaxHealth);
        health.ApplyDamage(13);

        WorldSessionPresentationState state = WorldSessionPresentationState.Create(
            camera: new Camera(new Vector3(2f, 3f, 4f)),
            visibleWorld: new GeometryRenderExport(
                Array.Empty<GeometryCellKey>(),
                Array.Empty<GeometryRenderCell>(),
                Array.Empty<GeometryRenderBlockDescriptor>(),
                Array.Empty<Cube>()),
            activeEnemies: Array.Empty<EnemyActor>(),
            mapCollisionBodies: Array.Empty<BulletSharp.RigidBody>(),
            playerCollisionBody: null,
            health,
            PlayerInteractionMode.Combat,
            derivedStats,
            playerStats);

        Assert.Equal(97, state.HudStatus.CurrentHealth);
        Assert.Equal(110, state.HudStatus.MaxHealth);
        Assert.Equal(PlayerInteractionMode.Combat, state.HudStatus.InteractionMode);
        Assert.Equal(33, state.HudStatus.MinDamage);
        Assert.Equal(66, state.HudStatus.MaxDamage);
        Assert.Equal(14, state.HudStatus.Strength);
        Assert.Equal(2.5f, state.HudStatus.AttackSpeed);
        Assert.Equal(12, state.HudStatus.Vitality);
    }
}
