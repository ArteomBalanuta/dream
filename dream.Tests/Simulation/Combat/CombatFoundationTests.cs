using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;

namespace Dream.Tests.Simulation.Combat;


public class HealthStateTests
{
    [Fact]
    public void ApplyDamage_Clamps_To_Zero_And_Reports_Fatal_Hit()
    {
        HealthState health = new(maxHealth: 100);

        DamageReceipt receipt = health.ApplyDamage(140);

        Assert.Equal(100, receipt.PreviousHealth);
        Assert.Equal(0, receipt.CurrentHealth);
        Assert.Equal(100, receipt.AppliedAmount);
        Assert.True(receipt.WasFatal);
        Assert.False(health.IsAlive);
    }

    [Theory]
    [InlineData(100, 1, 1)]
    [InlineData(100, 2, 1)]
    [InlineData(50, 1, 1)]
    public void SetMaxHealthPreservingRatio_NeverRoundsALivingActorDownToZero(int maxHealth, int currentHealth, int newMax)
    {
        HealthState health = new(maxHealth);
        health.SetAuthoritativeState(currentHealth, maxHealth);

        health.SetMaxHealthPreservingRatio(newMax);

        Assert.True(health.IsAlive);
        Assert.Equal(1, health.CurrentHealth);
    }

    [Fact]
    public void SetMaxHealthPreservingRatio_KeepsTheDeadDead_AndScalesTheLivingByRatio()
    {
        HealthState dead = new(100);
        dead.ApplyDamage(100);
        dead.SetMaxHealthPreservingRatio(10);
        Assert.False(dead.IsAlive);
        Assert.Equal(0, dead.CurrentHealth);

        HealthState half = new(100);
        half.SetAuthoritativeState(50, 100);
        half.SetMaxHealthPreservingRatio(10);
        Assert.Equal(5, half.CurrentHealth);
    }
}

public class CombatActorHandleTests
{
    [Fact]
    public void WorldAnchor_Comes_From_AnchorProvider()
    {
        TestCombatAnchorProvider anchorProvider = new(new Vector3(2f, 3f, 4f));
        HealthState health = new(maxHealth: 50);
        CombatActorHandle actor = new(
            Id: 17,
            Kind: CreatureKind.Object,
            Damageable: health,
            AnchorProvider: anchorProvider);

        Assert.Equal(17, actor.Id);
        Assert.Equal(CreatureKind.Object, actor.Kind);
        Assert.Equal(new Vector3(2f, 3f, 4f), actor.GetWorldAnchor());
    }

    private sealed class TestCombatAnchorProvider : ICombatAnchorProvider
    {
        private readonly Vector3 _anchor;

        public TestCombatAnchorProvider(Vector3 anchor)
        {
            _anchor = anchor;
        }

        public Vector3 GetCombatAnchorWorldPosition()
        {
            return _anchor;
        }
    }
}

public class PlayerCombatantTests
{
    [Fact]
    public void Creates_Player_Handle_With_Head_Level_Anchor_And_Health()
    {
        PlayerCombatant combatant = new(() => new Vector3(5f, 1.5f, 6f));

        DamageReceipt receipt = combatant.ApplyDamage(30);

        Assert.Equal(CreatureKind.Player, combatant.Actor.Kind);
        Assert.Equal(new Vector3(5f, 2.7f, 6f), combatant.GetCombatAnchorWorldPosition());
        // A default player is never naked in mitigation terms: Defense 2 (Agility 10 / 4) is armor, so
        // the 30 arrives as 30 * 100 / 102 = 29.41 -> 29 and 71 of the 100 is left.
        Assert.Equal(29, receipt.AppliedAmount);
        Assert.Equal(71, combatant.Health.CurrentHealth);
        Assert.False(receipt.WasFatal);
    }
}
