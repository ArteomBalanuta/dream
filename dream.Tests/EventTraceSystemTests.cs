using Xunit;

public class EventTraceSystemTests
{
    [Fact]
    public void Trace_System_Records_Damage_Event_Names()
    {
        SceneEventHub hub = new();
        EventTraceSystem trace = new(hub);

        hub.Publish(new DamageAppliedEvent(
            new CombatActorHandle(1, CreatureKind.Player, new HealthState(100), new FixedTraceAnchorProvider()),
            10,
            FloatingCombatTextKind.Regular));

        Assert.Contains(nameof(DamageAppliedEvent), trace.Entries);
    }

    [Fact]
    public void Trace_System_Records_Actor_Died_Event_Names()
    {
        SceneEventHub hub = new();
        EventTraceSystem trace = new(hub);
        PlayerCombatant combatant = new(() => OpenTK.Mathematics.Vector3.Zero, eventHub: hub);

        combatant.ApplyDamage(100);

        Assert.Contains(nameof(ActorDiedEvent), trace.Entries);
    }

    private sealed class FixedTraceAnchorProvider : ICombatAnchorProvider
    {
        public OpenTK.Mathematics.Vector3 GetCombatAnchorWorldPosition() => OpenTK.Mathematics.Vector3.Zero;
    }
}
