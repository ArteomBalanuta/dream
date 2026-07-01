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
        Assert.Equal(70, combatant.Health.CurrentHealth);
        Assert.Equal(30, receipt.AppliedAmount);
        Assert.False(receipt.WasFatal);
    }
}
