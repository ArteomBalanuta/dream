using OpenTK.Mathematics;
using Xunit;

public class EnemyCombatantTests
{
    [Fact]
    public void ApplyDamage_Publishes_Fatal_State_When_Health_Reaches_Zero()
    {
        SceneEventHub hub = new();
        List<ActorDiedEvent> observed = [];
        EnemyCombatant combatant = new(() => new Vector3(2f, 0f, 3f), hub, maxHealth: 30);

        hub.Subscribe<ActorDiedEvent>(evt => observed.Add(evt));
        DamageReceipt receipt = combatant.ApplyDamage(30);

        Assert.True(receipt.WasFatal);
        Assert.False(combatant.Health.IsAlive);
        Assert.Single(observed);
        Assert.Equal(combatant.Actor.Id, observed[0].Actor.Id);
    }

    [Fact]
    public void TryStartAttack_Uses_Cooldown_Gating()
    {
        EnemyCombatant combatant = new(() => Vector3.Zero, maxHealth: 30, attackDamage: 8, attackIntervalSeconds: 0.8f);

        Assert.True(combatant.TryStartAttack());
        Assert.False(combatant.TryStartAttack());

        combatant.AdvanceCombat(0.79f);
        Assert.False(combatant.TryStartAttack());

        combatant.AdvanceCombat(0.01f);
        Assert.True(combatant.TryStartAttack());
    }

    [Fact]
    public void CombatAnchor_Uses_Position_And_Anchor_Height()
    {
        EnemyCombatant combatant = new(() => new Vector3(5f, 1f, 6f), anchorHeightOffset: 0.9f);

        Assert.Equal(new Vector3(5f, 1.9f, 6f), combatant.GetCombatAnchorWorldPosition());
    }
}
