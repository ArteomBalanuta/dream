using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Replication;

// The client appliers run once per received snapshot. Before MEM-7 they allocated (bytes per
// call, Debug): RemoteEnemyStateApplier with three existing wolves 1064, of which EnemyRegistry's
// own lookups are 352 (after: 352); ClientCorrectionApplier on a six-actor snapshot 120 (after: 0);
// ApplyWorldMutations with two resolving CubeMoved deltas 256, with Map.UpdateCubePosition 0 on a
// bare map (after: 0); ApplySnapshotToRuntime with the same deltas on a client session 1416, of
// which the session map's own updates were 832 (after MEM-7: 832; after MEM-9: 400, all in
// WorldPhysicsBinder; after MEM-10: 0, so the whole call is 0).
public class ApplierAllocationTests
{
    private const string LocalSessionId = "session-local";

    private readonly ITestOutputHelper _output;

    public ApplierAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RemoteEnemyStateApplier_AllEnemiesExist_AllocatesNoMoreThanTheRegistryLookups()
    {
        EnemyRegistry registry = new();
        ActorSnapshot[] actors =
        [
            Enemy(3000, new Vector3(4f, 0f, 6f)),
            new ActorSnapshot(7, CreatureKind.Player, "session-a", Vector3.One, -Vector3.UnitZ, 100, 100, true),
            Enemy(3001, new Vector3(5f, 0f, 6f)),
            Enemy(3002, new Vector3(6f, 0f, 6f))
        ];
        RemoteEnemyStateApplier.ApplyActorSnapshots(registry, actors);
        Assert.Equal(3, registry.ActiveActors.Count);

        // EnemyRegistry (owned by a later wave) still allocates in TryGetEnemyByActorId and
        // RemoveAllExcept. The applier must add nothing on top of exactly those calls.
        HashSet<int> active = [3000, 3001, 3002];
        long registryBytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            registry.TryGetEnemyByActorId(3000, out _);
            registry.TryGetEnemyByActorId(3001, out _);
            registry.TryGetEnemyByActorId(3002, out _);
            registry.RemoveAllExcept(active);
        });

        long bytes = AllocationAssert.AtMost(
            registryBytes,
            () => RemoteEnemyStateApplier.ApplyActorSnapshots(registry, actors));

        _output.WriteLine($"ApplyActorSnapshots: {bytes} B per call; registry calls alone: {registryBytes} B");
        Assert.Equal(3, registry.ActiveActors.Count);
    }

    [Fact]
    public void ClientCorrectionApplier_TypicalSnapshot_AllocatesNothing()
    {
        using ClientWorldSession runtime = CreateClient();
        ClientPredictionReconciler reconciler = new();
        ServerSnapshotEnvelope snapshot = new(
            5,
            new Dictionary<string, ulong>(StringComparer.Ordinal) { [LocalSessionId] = 3 },
            [
                Enemy(3000, new Vector3(4f, 0f, 6f)),
                new ActorSnapshot(8, CreatureKind.Player, "session-other", Vector3.One, -Vector3.UnitZ, 100, 100, true),
                Enemy(3001, new Vector3(5f, 0f, 6f)),
                new ActorSnapshot(9, CreatureKind.Player, "session-third", Vector3.One, -Vector3.UnitZ, 100, 100, true),
                Enemy(3002, new Vector3(6f, 0f, 6f)),
                new ActorSnapshot(
                    7,
                    CreatureKind.Player,
                    LocalSessionId,
                    runtime.Player.GetWorldPosition(),
                    -Vector3.UnitZ,
                    90,
                    100,
                    true,
                    PlayerInteractionMode.Building)
            ],
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        long bytes = AllocationAssert.AtMost(
            0,
            () => ClientCorrectionApplier.ApplySnapshotToRuntime(runtime, snapshot, LocalSessionId, reconciler));

        _output.WriteLine($"ApplySnapshotToRuntime (correction): {bytes} B per call");
        Assert.Equal(90, runtime.Player.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void ApplyWorldMutations_ResolvingCubeMoves_AllocatesNoMoreThanTheMapUpdates()
    {
        Map map = new(width: 10, depth: 10);
        Cube first = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: false));
        Cube second = Assert.IsType<Cube>(map.TryAddCube(new Vector3(5f, 2f, 3f), withPhysics: false));
        map.AssignReplicatedCubeId(first, 10);
        map.AssignReplicatedCubeId(second, 11);
        WorldMutationDelta[] even = CreateMoves(0.5f);
        WorldMutationDelta[] odd = CreateMoves(0.6f);

        // Map.UpdateCubePosition (deferred, not owned here) allocates on its own; measure the same
        // two updates directly so the applier is held to adding nothing on top of them.
        int mapToggle = 0;
        long mapBytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            float offset = (mapToggle++ & 1) == 0 ? 0.5f : 0.6f;
            map.UpdateCubePosition(first, new Vector3(3f + offset, 2.5f, 3.5f), syncPhysics: true);
            map.UpdateCubePosition(second, new Vector3(5f + offset, 2.5f, 3.5f), syncPhysics: true);
        });

        int toggle = 0;
        long bytes = AllocationAssert.AtMost(
            mapBytes,
            () => RemoteWorldMutationApplier.ApplyWorldMutations(map, (toggle++ & 1) == 0 ? even : odd));

        _output.WriteLine($"ApplyWorldMutations: {bytes} B per call; Map updates alone: {mapBytes} B");
        Assert.Same(first, map.TryGetCubeAtCell(3, 2, 3));
        Assert.Same(second, map.TryGetCubeAtCell(5, 2, 3));
    }

    [Fact]
    public void ApplySnapshotToRuntime_UnsequencedCubeMoves_AllocatesNoMoreThanTheMapUpdates()
    {
        using ClientWorldSession runtime = CreateClient();
        Cube first = Assert.IsType<Cube>(runtime.Map.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: false));
        Cube second = Assert.IsType<Cube>(runtime.Map.TryAddCube(new Vector3(5f, 2f, 3f), withPhysics: false));
        runtime.Map.AssignReplicatedCubeId(first, 10);
        runtime.Map.AssignReplicatedCubeId(second, 11);
        ServerSnapshotEnvelope even = CreateMoveSnapshot(CreateMoves(0.5f));
        ServerSnapshotEnvelope odd = CreateMoveSnapshot(CreateMoves(0.6f));

        // The session map's synced moves, WorldPhysicsBinder included, allocate nothing (MEM-10).
        int mapToggle = 0;
        long mapBytes = AllocationAssert.AtMost(0, () =>
        {
            float offset = (mapToggle++ & 1) == 0 ? 0.5f : 0.6f;
            runtime.Map.UpdateCubePosition(first, new Vector3(3f + offset, 2.5f, 3.5f), syncPhysics: true);
            runtime.Map.UpdateCubePosition(second, new Vector3(5f + offset, 2.5f, 3.5f), syncPhysics: true);
        });

        int toggle = 0;
        long bytes = AllocationAssert.AtMost(
            mapBytes,
            () => RemoteWorldMutationApplier.ApplySnapshotToRuntime(runtime, (toggle++ & 1) == 0 ? even : odd));

        _output.WriteLine($"ApplySnapshotToRuntime (world): {bytes} B per call; Map updates alone: {mapBytes} B");
    }

    [Fact]
    public void ClientSnapshotCastApplier_PeerCasts_AllocatesNoMoreThanTheHubPublishes()
    {
        SceneEventHub hub = new();
        int seen = 0;
        hub.Subscribe<Dream.Rendering.Effects.SkillCastEvent>(_ => seen++);
        CastEventDelta[] casts =
        [
            new(9, Dream.Simulation.Content.SkillKind.MeleeArc, new Vector3(4f, 1f, 2f), new Vector3(3f, 0f, 4f), 3.5f),
            new(7, Dream.Simulation.Content.SkillKind.RangedBolt, new Vector3(4f, 1f, 2f), Vector3.UnitX, 18f),
            new(10, Dream.Simulation.Content.SkillKind.SelfHeal, new Vector3(4f, 1f, 2f), Vector3.Zero, 0f),
            new(10, (Dream.Simulation.Content.SkillKind)99, new Vector3(4f, 1f, 2f), Vector3.Zero, 0f)
        ];
        ServerSnapshotEnvelope snapshot = new(
            5,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>(),
            CastEvents: casts);

        // SceneEventHub.Publish (Simulation, not owned here) copies its handler list on every publish, so
        // the two events that draw cost the hub's own 32 B each. Measure exactly those two publishes
        // directly so the applier is held to adding nothing on top of them, as the damage path is.
        Dream.Rendering.Effects.SkillCastEvent sample = new(Dream.Rendering.Effects.SkillFxKind.Arc, Vector3.One, Vector3.UnitX, 3.5f);
        long hubBytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            hub.Publish(sample);
            hub.Publish(sample);
        });

        long bytes = AllocationAssert.AtMost(hubBytes, () => ClientSnapshotCastApplier.Apply(hub, snapshot, 7));

        // The paths that publish nothing are held to exactly 0: absent list, the caster's own cast, a bad event.
        ServerSnapshotEnvelope absent = snapshot with { CastEvents = null };
        ServerSnapshotEnvelope ownAndMalformed = snapshot with
        {
            CastEvents = [casts[1], casts[3]]
        };
        long nothing = AllocationAssert.AtMost(0, () =>
        {
            ClientSnapshotCastApplier.Apply(hub, absent, 7);
            ClientSnapshotCastApplier.Apply(hub, ownAndMalformed, 7);
        });

        _output.WriteLine($"ClientSnapshotCastApplier.Apply: {bytes} B per call; hub publishes alone: {hubBytes} B; no-publish paths: {nothing} B");
        Assert.True(seen > 0);
    }

    private static WorldMutationDelta[] CreateMoves(float offset)
    {
        return
        [
            WorldMutationDelta.Moved(new Vector3(3f, 2f, 3f), new Vector3(3f + offset, 2.5f, 3.5f)) with { ActorId = 10 },
            WorldMutationDelta.Moved(new Vector3(5f, 2f, 3f), new Vector3(5f + offset, 2.5f, 3.5f)) with { ActorId = 11 }
        ];
    }

    private static ServerSnapshotEnvelope CreateMoveSnapshot(WorldMutationDelta[] mutations)
    {
        return new ServerSnapshotEnvelope(
            5,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            mutations,
            Array.Empty<DamageEventDelta>());
    }

    private static ActorSnapshot Enemy(int actorId, Vector3 position)
    {
        return new ActorSnapshot(
            actorId,
            CreatureKind.Enemy,
            null,
            position,
            Vector3.UnitX,
            40,
            50,
            true,
            DisplayName: "Wolf",
            EnemyBehaviorState: EnemyBehaviorState.Chasing);
    }

    private static ClientWorldSession CreateClient()
    {
        return new ClientWorldSession(12, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
    }
}
