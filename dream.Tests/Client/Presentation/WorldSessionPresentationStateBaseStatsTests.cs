using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Client.Presentation;

// Wave H3 (correction 6): the HUD carries the base stats beside the effective ones, so a slider
// seeds from the base and a gear bonus is never saved as a base value.
public class WorldSessionPresentationStateBaseStatsTests
{
    private static WorldSessionPresentationState Create(PlayerCombatStats effective, PlayerCombatStats? baseStats, int armor)
    {
        PlayerCombatDerivedStats derived = effective.Derive();
        return WorldSessionPresentationState.Create(
            new Camera(Vector3.Zero),
            new GeometryRenderExport(
                Array.Empty<GeometryCellKey>(),
                Array.Empty<GeometryRenderCell>(),
                Array.Empty<GeometryRenderBlockDescriptor>(),
                Array.Empty<Cube>()),
            Array.Empty<EnemyActor>(),
            Array.Empty<BulletSharp.RigidBody>(),
            null,
            new HealthState(derived.MaxHealth),
            PlayerInteractionMode.Combat,
            derived,
            effective,
            1,
            0,
            100,
            default,
            baseStats,
            armor);
    }

    [Fact]
    public void HudStatus_CarriesBaseAndEffectiveStatsApart_AndArmor()
    {
        PlayerCombatStats effective = new() { Strength = 15, AttackSpeed = 2.75f, Vitality = 13 };
        PlayerCombatStats baseStats = new() { Strength = 10, AttackSpeed = 2.0f, Vitality = 10 };

        var status = Create(effective, baseStats, armor: 9).HudStatus;

        Assert.Equal(15, status.Strength);
        Assert.Equal(10, status.BaseStrength);
        Assert.Equal(2.75f, status.AttackSpeed);
        Assert.Equal(2.0f, status.BaseAttackSpeed);
        Assert.Equal(13, status.Vitality);
        Assert.Equal(10, status.BaseVitality);
        Assert.Equal(9, status.Armor);
    }

    [Fact]
    public void HudStatus_WithoutABaseBlock_FallsBackToTheEffectiveStats()
    {
        PlayerCombatStats effective = new() { Strength = 15, AttackSpeed = 2.75f, Vitality = 13 };

        var status = Create(effective, null, armor: 0).HudStatus;

        Assert.Equal(15, status.BaseStrength);
        Assert.Equal(2.75f, status.BaseAttackSpeed);
        Assert.Equal(13, status.BaseVitality);
    }
}
