using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation.Combat;


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

    [Fact]
    public void Demo_Combat_Target_Anchors_In_Front_Of_Camera()
    {
        Camera camera = new(new Vector3(5f, 2f, 7f));

        CombatActorHandle actor = CombatDemoTargetFactory.CreateImpactActor(camera, distance: 2f, verticalOffset: 0.15f);

        Assert.Equal(CreatureKind.Object, actor.Kind);
        Assert.Equal(new Vector3(5f, 2.15f, 5f), actor.GetWorldAnchor());
    }
}
