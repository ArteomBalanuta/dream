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
        // Fatal means fatal *after* mitigation. A default player has Defense 2 (Agility 10 / 4) and so
        // 100 / 102 of every hit lands: 102 * 100 / 102 = 100 exactly, which is the whole 100 max health.
        DamageReceipt receipt = combatant.ApplyDamage(102);

        Assert.Equal(100, receipt.AppliedAmount);
        Assert.True(receipt.WasFatal);
        ActorDiedEvent deathEvent = Assert.Single(observed);
        Assert.Equal(combatant.Actor.Id, deathEvent.Actor.Id);
    }

    // The surprise that let the test above rot for two days, pinned as a fact: the hit that used to kill
    // a fresh player exactly no longer does, because Defense is folded into mitigation.
    // 100 * 100 / 102 = 98.04 -> 98, so 2 health and no death event.
    [Fact]
    public void AHundredDamage_OnAFreshPlayer_Applies98_AndIsSurvived()
    {
        SceneEventHub hub = new();
        List<ActorDiedEvent> observed = [];
        PlayerCombatant combatant = new(() => Vector3.Zero, eventHub: hub);
        hub.Subscribe<ActorDiedEvent>(evt => observed.Add(evt));

        Assert.Equal(100, combatant.Health.MaxHealth);
        DamageReceipt receipt = combatant.ApplyDamage(100);

        Assert.Equal(98, receipt.AppliedAmount);
        Assert.False(receipt.WasFatal);
        Assert.Equal(2, combatant.Health.CurrentHealth);
        Assert.Empty(observed);
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
