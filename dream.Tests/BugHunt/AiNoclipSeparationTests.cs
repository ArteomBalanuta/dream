using System.Reflection;
using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-AI-5: ResolvePlayerEnemySeparation exempted noclip only by comparing the target's actor id with the
// primary seat's, so a secondary seat flying at a wolf's body height was still shoved ~0.95 m a tick.
// Every seat holds its own Player with its own noclip toggle, so the exemption is read off the target.
public sealed class AiNoclipSeparationTests
{
    private static readonly MethodInfo ResolveSeparation = typeof(WorldSessionRuntime).GetMethod(
        "ResolvePlayerEnemySeparation",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static WorldSessionRuntime CreateRuntimeWithOneWolfAt(Vector3 wolfPosition)
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.WriteCheckpointOnDispose = false;
        runtime.Enemies.Clear();
        EnemyCombatant combatant = new(
            () => wolfPosition,
            new SceneEventHub(),
            actorId: 2500,
            maxHealth: 100,
            attackDamage: 1,
            attackIntervalSeconds: 1f);
        runtime.Enemies.Add(new EnemyActor(
            combatant,
            wolfPosition,
            aggroRange: 0f,
            attackRange: 0f,
            moveSpeed: 0f,
            displayName: "Wolf",
            level: 1,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight));
        return runtime;
    }

    private static Seat Resolve(WorldSessionRuntime runtime, Vector3 position, bool noclip)
    {
        Seat seat = new(position, noclip);
        List<IAuthoritativePlayerTarget> targets = [seat];
        ResolveSeparation.Invoke(runtime, [targets]);
        return seat;
    }

    [Fact]
    public void ASecondarySeatInNoclip_IsNotShovedByAWolfAtItsOwnHeight()
    {
        Vector3 wolf = new(20f, 1f, 20f);
        using WorldSessionRuntime runtime = CreateRuntimeWithOneWolfAt(wolf);

        // Standing in the wolf, well inside the 0.95 m separation, and vertically overlapping it.
        Seat flying = Resolve(runtime, new Vector3(20.1f, 1.3f, 20f), noclip: true);

        Assert.Null(flying.MovedTo);
    }

    // The control: the same seat with noclip off is still pushed out, so the push itself is intact.
    [Fact]
    public void ASecondarySeatWithoutNoclip_IsStillShoved()
    {
        Vector3 wolf = new(20f, 1f, 20f);
        using WorldSessionRuntime runtime = CreateRuntimeWithOneWolfAt(wolf);

        Seat walking = Resolve(runtime, new Vector3(20.1f, 1.3f, 20f), noclip: false);

        Assert.NotNull(walking.MovedTo);
        Assert.True(
            (walking.MovedTo!.Value.Xz - wolf.Xz).Length > (new Vector2(20.1f, 20f) - wolf.Xz).Length,
            "the push moves the seat away from the wolf");
    }

    private sealed class Seat(Vector3 position, bool noclip) : IAuthoritativePlayerTarget
    {
        public PlayerSessionId SessionId { get; } = new("secondary");

        // Deliberately not the primary seat's actor id: that is what the old check keyed on.
        public int ActorId => 9100;

        public Vector3 Position { get; } = position;

        public Vector3 Facing => -Vector3.UnitZ;

        public Vector3 EyePosition => Position;

        public PlayerCombatant Combatant { get; } = new(() => position);

        public PlayerInteractionMode InteractionMode => PlayerInteractionMode.Combat;

        public bool IsAlive => true;

        public bool IsSpawned => true;

        public bool IsNoclipEnabled { get; } = noclip;

        public Vector3? MovedTo { get; private set; }

        public void SetWorldPosition(Vector3 position) => MovedTo = position;
    }
}
