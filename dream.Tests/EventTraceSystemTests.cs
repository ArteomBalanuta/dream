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
        PlayerCombatant combatant = new(() => OpenTK.Mathematics.Vector3.Zero);

        hub.Publish(new ActorDiedEvent(combatant.Actor));

        Assert.Contains(nameof(ActorDiedEvent), trace.Entries);
    }

    [Fact]
    public void Trace_System_Records_Cube_Placed_Event_Names()
    {
        SceneEventHub hub = new();
        EventTraceSystem trace = new(hub);
        Cube cube = new(isEmpty: false, isSolid: true) { Position = new OpenTK.Mathematics.Vector3(2.5f, 1.5f, 3.5f) };

        hub.Publish(new CubePlacedEvent(cube));

        Assert.Contains(nameof(CubePlacedEvent), trace.Entries);
    }

    [Fact]
    public void Trace_System_Records_Cube_Destroyed_Event_Names()
    {
        SceneEventHub hub = new();
        EventTraceSystem trace = new(hub);
        Cube cube = new(isEmpty: false, isSolid: true) { Position = new OpenTK.Mathematics.Vector3(4.5f, 1.5f, 6.5f) };

        hub.Publish(new CubeDestroyedEvent(cube));

        Assert.Contains(nameof(CubeDestroyedEvent), trace.Entries);
    }

    [Fact]
    public void Trace_System_Keeps_A_Bounded_Recent_History()
    {
        SceneEventHub hub = new();
        EventTraceSystem trace = new(hub, maxEntries: 2);
        CombatActorHandle actor = new(1, CreatureKind.Player, new HealthState(100), new FixedTraceAnchorProvider());

        hub.Publish(new DamageAppliedEvent(actor, 10, FloatingCombatTextKind.Regular));
        hub.Publish(new ActorDiedEvent(actor));
        hub.Publish(new DamageAppliedEvent(actor, 15, FloatingCombatTextKind.Regular));

        Assert.Equal([nameof(ActorDiedEvent), nameof(DamageAppliedEvent)], trace.Entries);
    }

    private sealed class FixedTraceAnchorProvider : ICombatAnchorProvider
    {
        public OpenTK.Mathematics.Vector3 GetCombatAnchorWorldPosition() => OpenTK.Mathematics.Vector3.Zero;
    }
}
