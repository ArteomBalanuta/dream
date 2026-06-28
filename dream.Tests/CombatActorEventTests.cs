using OpenTK.Mathematics;
using Xunit;

public class CombatActorEventTests
{
    [Fact]
    public void Actor_Died_Event_Publishes_Combatant_Handle_Through_Scene_Hub()
    {
        SceneEventHub hub = new();
        List<ActorDiedEvent> observed = [];
        PlayerCombatant combatant = new(() => Vector3.Zero);

        hub.Subscribe<ActorDiedEvent>(evt => observed.Add(evt));
        hub.Publish(new ActorDiedEvent(combatant.Actor));

        ActorDiedEvent deathEvent = Assert.Single(observed);
        Assert.Equal(combatant.Actor.Id, deathEvent.Actor.Id);
    }
}
