using OpenTK.Mathematics;
using Xunit;

public class CombatActorEventTests
{
    [Fact]
    public void Fatal_Damage_Publishes_Actor_Died_Event()
    {
        SceneEventHub hub = new();
        List<ActorDiedEvent> observed = [];
        PlayerCombatant combatant = new(() => Vector3.Zero, eventHub: hub);

        hub.Subscribe<ActorDiedEvent>(evt => observed.Add(evt));
        DamageReceipt receipt = combatant.ApplyDamage(100);

        Assert.True(receipt.WasFatal);
        ActorDiedEvent deathEvent = Assert.Single(observed);
        Assert.Equal(combatant.Actor.Id, deathEvent.Actor.Id);
    }
}
