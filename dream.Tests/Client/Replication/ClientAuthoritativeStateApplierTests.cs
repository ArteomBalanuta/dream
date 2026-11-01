using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Client.Replication;

// The join-accepted cases that rebuild a full startup world live in
// ClientJoinAcceptedStartupTerrainApplierTests and ClientJoinAcceptedSubcellApplierTests so they
// run in parallel with this class; they use the helpers here.
public class ClientAuthoritativeStateApplierTests
{
    [Fact]
    public void ClientCorrectionApplier_ApplyActorSnapshot_UpdatesReplicatedPlayerTarget()
    {
        FakeReplicatedPlayerTarget target = new();

        ActorSnapshot snapshot = new(
            1,
            CreatureKind.Player,
            "session-a",
            new Vector3(7f, 1.5f, 4f),
            Vector3.UnitX,
            65,
            120,
            true);

        ClientCorrectionApplier.ApplyActorSnapshot(target, snapshot);

        Assert.Equal(7f, target.Position.X, 3);
        Assert.Equal(1.5f, target.Position.Y, 3);
        Assert.Equal(4f, target.Position.Z, 3);
        Assert.Equal(65, target.CurrentHealth);
        Assert.Equal(120, target.MaxHealth);
        Assert.Equal(1f, target.Facing.X, 3);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_ReconcilesPlacedCubes()
    {
        Map map = new(width: 10, depth: 10);
        map.TryAddCube(new Vector3(8f, 1f, 8f), withPhysics: false);

        WorldSnapshot snapshot = new(
            10,
            8,
            10,
            [
                new Vector3(2f, 1f, 2f),
                new Vector3(3f, 1f, 3f)
            ]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        Assert.NotNull(map.TryGetCubeAtCell(2, 1, 2));
        Assert.NotNull(map.TryGetCubeAtCell(3, 1, 3));
        Assert.Null(map.TryGetCubeAtCell(8, 1, 8));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_Preserves_PlacedCube_Material()
    {
        Map map = new(width: 10, depth: 10);
        Vector3 cell = new(2f, 1f, 2f);
        WorldSnapshot snapshot = new(
            10,
            8,
            10,
            [cell],
            PlacedCubes:
            [
                new PlacedCubeSnapshot(
                    cell,
                    new Vector3(2.5f, 1.5f, 2.5f),
                    GeometryMaterialId.Stone)
            ]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(
            map.TryGetGeometryCellAtCell(2, 1, 2));
        Assert.Equal(GeometryMaterialId.Stone, geometryCell.Definition.MaterialId);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_AppliesWorldMutations()
    {
        Map map = new(width: 10, depth: 10);
        map.TryAddCube(new Vector3(4f, 1f, 4f), withPhysics: false);

        WorldMutationDelta[] mutations =
        [
            new WorldMutationDelta(WorldMutationKind.CubeDestroyed, new Vector3(4f, 1f, 4f)),
            new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(6f, 1f, 6f))
        ];

        RemoteWorldMutationApplier.ApplyWorldMutations(map, mutations);

        Assert.Null(map.TryGetCubeAtCell(4, 1, 4));
        Assert.NotNull(map.TryGetCubeAtCell(6, 1, 6));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_Preserves_PlacedCube_Material()
    {
        Map map = new(width: 10, depth: 10);

        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [
                new WorldMutationDelta(
                    WorldMutationKind.CubePlaced,
                    new Vector3(6f, 1f, 6f),
                    MaterialId: GeometryMaterialId.Dirt)
            ]);

        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(
            map.TryGetGeometryCellAtCell(6, 1, 6));
        Assert.Equal(GeometryMaterialId.Dirt, geometryCell.Definition.MaterialId);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_AddsReplicatedPlacedCubesAsDynamicBodies()
    {
        // A locally simulated map (no replicated cube bodies), as the authoritative runtime builds it.
        using PhysicsIntegrator physics = new();
        Map map = CreateSimulatedMap(physics);

        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(6f, 1f, 6f))]);

        Cube cube = Assert.IsType<Cube>(map.TryGetCubeAtCell(6, 1, 6));
        Assert.Contains(
            physics.DynamicMapCubeBodies,
            body => body.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_AppliesAuthoritativeCubeMovedMutation()
    {
        Map map = new(width: 10, depth: 10);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(4f, 1f, 4f), withPhysics: false));
        Vector3 authoritativeCenter = new(5.2f, 1.5f, 4.65f);

        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [WorldMutationDelta.Moved(new Vector3(4f, 1f, 4f), authoritativeCenter)]);

        Assert.Null(map.TryGetCubeAtCell(4, 1, 4));
        Assert.Same(cube, map.TryGetCubeAtCell(5, 1, 4));
        AssertVector3(authoritativeCenter, cube.Position);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_ReconcilesFracturedCubeCells()
    {
        Map map = new(width: 10, depth: 10);
        map.TryAddCube(new Vector3(8f, 1f, 8f), withPhysics: false);

        WorldSnapshot snapshot = new(
            10,
            8,
            10,
            [new Vector3(2f, 1f, 2f)],
            [new Vector3(3f, 1f, 3f)]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        GeometryCellRecord fracturedCell = Assert.IsType<GeometryCellRecord>(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);

        Assert.NotNull(map.TryGetCubeAtCell(2, 1, 2));
        Assert.NotNull(fracturedCell.SubdivisionState);
        Assert.Contains(export.DynamicBlocks, block => block.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.Null(map.TryGetCubeAtCell(8, 1, 8));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_UsesAuthoritativeFracturePieces()
    {
        Map map = new(width: 10, depth: 10);
        GeometryFracturePiece[] pieces =
        [
            new(new Vector3(3.2f, 1.2f, 3.2f), 0.2f),
            new(new Vector3(3.7f, 1.3f, 3.65f), 0.24f)
        ];
        WorldSnapshot snapshot = new(
            10,
            8,
            10,
            Array.Empty<Vector3>(),
            [new Vector3(3f, 1f, 3f)],
            FracturedCubes:
            [
                new FracturedCubeSnapshot(new Vector3(3f, 1f, 3f), GeometryMaterialId.Stone, pieces)
            ]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        Assert.Equal(2, export.DynamicBlocks.Count(block => block.CellKey == new GeometryCellKey(3, 1, 3)));
        Assert.Contains(export.DynamicBlocks, block =>
            block.MaterialId == GeometryMaterialId.Stone &&
            block.Center == pieces[0].Center &&
            block.SideLength == pieces[0].SideLength);
        Assert.Contains(export.DynamicBlocks, block =>
            block.MaterialId == GeometryMaterialId.Stone &&
            block.Center == pieces[1].Center &&
            block.SideLength == pieces[1].SideLength);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_AppliesCubeFractureMutation()
    {
        Map map = new(width: 10, depth: 10);
        map.TryAddCube(new Vector3(4f, 1f, 4f), withPhysics: false);

        WorldMutationDelta[] mutations =
        [
            new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(4f, 1f, 4f))
        ];

        RemoteWorldMutationApplier.ApplyWorldMutations(map, mutations);

        GeometryCellRecord fracturedCell = Assert.IsType<GeometryCellRecord>(map.TryGetRubbleAtCell(new GeometryCellKey(4, 1, 4)));
        Assert.Null(map.TryGetCubeAtCell(4, 1, 4));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(4.5f, 1.5f, 4.5f), 4f);

        Assert.NotNull(fracturedCell.SubdivisionState);
        Assert.Contains(export.DynamicBlocks, block => block.CellKey == new GeometryCellKey(4, 1, 4));
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(4.5f, 1.5f, 4.5f));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_UsesAuthoritativeFracturePieces()
    {
        Map map = new(width: 10, depth: 10);
        map.TryAddCube(new Vector3(4f, 1f, 4f), withPhysics: false);
        GeometryFracturePiece[] pieces =
        [
            new(new Vector3(4.18f, 1.18f, 4.22f), 0.18f),
            new(new Vector3(4.72f, 1.26f, 4.65f), 0.22f)
        ];

        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [WorldMutationDelta.Fractured(new Vector3(4f, 1f, 4f), GeometryMaterialId.Stone, pieces)]);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(4.5f, 1.5f, 4.5f), 4f);
        GeometryRenderBlockDescriptor[] dynamicBlocks = export.DynamicBlocks
            .Where(block => block.CellKey == new GeometryCellKey(4, 1, 4))
            .OrderBy(block => block.Center.X)
            .ToArray();
        Assert.Equal(2, dynamicBlocks.Length);
        Assert.Equal(GeometryMaterialId.Stone, dynamicBlocks[0].MaterialId);
        Assert.Contains(dynamicBlocks, block => block.Center == pieces[0].Center && block.SideLength == pieces[0].SideLength);
        Assert.Contains(dynamicBlocks, block => block.Center == pieces[1].Center && block.SideLength == pieces[1].SideLength);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplySnapshotToRuntime_AddsCollisionBodiesForReplicatedPlacedCubes()
    {
        using ClientWorldSession runtime = CreateRuntime();
        int initialCollisionBodyCount = runtime.Physics.MapCubeBodies.Count;
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            new WorldSnapshot(
                runtime.Map.Width,
                runtime.Map.Height,
                runtime.Map.Depth,
                [new Vector3(6f, 1f, 6f)]),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(runtime, snapshot);

        Assert.NotNull(runtime.Map.TryGetCubeAtCell(6, 1, 6));
        Assert.True(runtime.Physics.MapCubeBodies.Count > initialCollisionBodyCount);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_AddsPlacedCubesAsDynamicBodiesOnSimulatedMap()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateSimulatedMap(physics);
        WorldSnapshot snapshot = new(
            map.Width,
            map.Height,
            map.Depth,
            [new Vector3(6f, 1f, 6f)]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        Cube cube = Assert.IsType<Cube>(map.TryGetCubeAtCell(6, 1, 6));
        Assert.Contains(
            physics.DynamicMapCubeBodies,
            body => body.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplySnapshotToRuntime_AddsReplicatedPlacedCubesAsKinematicBodies()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            new WorldSnapshot(
                runtime.Map.Width,
                runtime.Map.Height,
                runtime.Map.Depth,
                [new Vector3(6f, 1f, 6f)]),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(runtime, snapshot);

        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(6, 1, 6));
        BulletSharp.RigidBody body = Assert.Single(
            runtime.Physics.MapCubeBodies,
            candidate => candidate.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
        Assert.True(body.CollisionFlags.HasFlag(BulletSharp.CollisionFlags.KinematicObject));
        Assert.DoesNotContain(body, runtime.Physics.DynamicMapCubeBodies);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_AlignsDynamicCubeRenderAndCollisionToAuthoritativeCenter()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateSimulatedMap(physics);
        Vector3 cell = new(6f, 1f, 6f);
        Vector3 authoritativeCenter = new(6.72f, 1.5f, 6.18f);
        WorldSnapshot snapshot = new(
            map.Width,
            map.Height,
            map.Depth,
            [cell],
            PlacedCubes: [new PlacedCubeSnapshot(cell, authoritativeCenter)]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        Cube cube = Assert.IsType<Cube>(map.TryGetCubeAtCell(6, 1, 6));
        AssertVector3(authoritativeCenter, cube.Position);
        GeometryRenderExport export = map.CaptureGeometryRenderExport(authoritativeCenter, 2f);
        Assert.Contains(
            export.DynamicCompatibilityCubes,
            candidate => ReferenceEquals(candidate, cube) && ApproximatelyEqual(candidate.Position, authoritativeCenter));
        BulletSharp.RigidBody body = Assert.Single(
            physics.DynamicMapCubeBodies,
            candidate => candidate.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
        body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
        Assert.Equal(authoritativeCenter.X, transform.Origin.X, 3);
        Assert.Equal(authoritativeCenter.Y, transform.Origin.Y, 3);
        Assert.Equal(authoritativeCenter.Z, transform.Origin.Z, 3);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplySnapshotToRuntime_AlignsReplicatedCubeRenderAndCollisionToAuthoritativeCenter()
    {
        using ClientWorldSession runtime = CreateRuntime();
        Vector3 cell = new(6f, 1f, 6f);
        Vector3 authoritativeCenter = new(6.72f, 1.5f, 6.18f);
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            new WorldSnapshot(
                runtime.Map.Width,
                runtime.Map.Height,
                runtime.Map.Depth,
                [cell],
                PlacedCubes: [new PlacedCubeSnapshot(cell, authoritativeCenter)]),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(runtime, snapshot);

        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(6, 1, 6));
        AssertVector3(authoritativeCenter, cube.Position);
        GeometryRenderExport export = runtime.Map.CaptureGeometryRenderExport(authoritativeCenter, 2f);
        Assert.Contains(
            export.DynamicCompatibilityCubes,
            candidate => ReferenceEquals(candidate, cube) && ApproximatelyEqual(candidate.Position, authoritativeCenter));
        BulletSharp.RigidBody body = Assert.Single(
            runtime.Physics.MapCubeBodies,
            candidate => candidate.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
        Assert.True(body.CollisionFlags.HasFlag(BulletSharp.CollisionFlags.KinematicObject));
        body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
        Assert.Equal(authoritativeCenter.X, transform.Origin.X, 3);
        Assert.Equal(authoritativeCenter.Y, transform.Origin.Y, 3);
        Assert.Equal(authoritativeCenter.Z, transform.Origin.Z, 3);
    }

    [Fact]
    public void ClientCorrectionApplier_ApplySnapshotMetadata_UpdatesAuthoritativeInteractionMode()
    {
        FakeReplicatedSessionStateTarget target = new();
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ClientCorrectionApplier.ApplySnapshotMetadata(target, snapshot);

        Assert.Equal(PlayerInteractionMode.Combat, target.InteractionMode);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_LeavesAcceptedWorldMutationToTheSnapshotStream()
    {
        // The accepted build reaches the world only through its sequenced CubePlaced delta, which
        // carries the material and replicated id; the bare action result must not change the map.
        using ClientWorldSession runtime = CreateRuntime();
        Assert.Null(runtime.Map.TryGetCubeAtCell(6, 1, 6));
        ulong checksumBefore = WorldStateChecksum.Compute(runtime.Map);
        AuthoritativeActionResult result = new(
            "session-a",
            17,
            41,
            Accepted: true,
            Payload: new WorldMutationResult(
                WorldMutationKind.CubePlaced,
                new Vector3(6f, 1f, 6f),
                Applied: true));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result);

        Assert.Null(runtime.Map.TryGetCubeAtCell(6, 1, 6));
        Assert.Equal(checksumBefore, WorldStateChecksum.Compute(runtime.Map));

        RemoteWorldMutationApplier.ApplyWorldMutations(
            runtime.Map,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(6f, 1f, 6f), MaterialId: GeometryMaterialId.Stone, Sequence: 1, ActorId: 99)]);

        Assert.Equal(GeometryMaterialId.Stone, runtime.Map.TryGetGeometryCellAtCell(6, 1, 6)?.Definition.MaterialId);
        Assert.True(runtime.Map.TryGetCubeByReplicatedId(99, out _));
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_PublishesNoDamageEventForAcceptedCombatResult()
    {
        using ClientWorldSession runtime = CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(evt => observed.Add(evt));
        AuthoritativeActionResult result = new(
            "session-a",
            19,
            44,
            Accepted: true,
            Payload: new CombatResolutionResult(
                runtime.Player.Combatant.Actor.Id,
                CreatureKind.Player,
                DamageApplied: 9,
                TargetDied: false,
                CurrentHealth: 91,
                MaxHealth: 100));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, events, result);

        // The snapshot's damage event is the one source of floating numbers (one number per hit).
        Assert.Empty(observed);
    }

    [Fact]
    public void FreshRuntimeWithNoStoredStats_ShowsTheServerStatsAfterTheFirstCorrection()
    {
        using ClientWorldSession runtime = CreateRuntime();
        int initialStrength = runtime.Player.Combatant.Stats.Strength;
        float initialAttackSpeed = runtime.Player.Combatant.Stats.AttackSpeed;
        AuthoritativeActionResult correction = new(
            "session-a",
            1,
            1,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(new PlayerCombatStatsSnapshot(23, initialAttackSpeed, 19)));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), correction);

        Assert.NotEqual(23, initialStrength);
        Assert.Equal(23, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(19, runtime.Player.Combatant.Stats.Vitality);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_AppliesPlayerStatsUpdateToRuntime()
    {
        using ClientWorldSession runtime = CreateRuntime();
        AuthoritativeActionResult result = new(
            "session-a",
            20,
            45,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(new PlayerCombatStatsSnapshot(18, 3.5f, 14)));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result);

        Assert.Equal(18, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(3.5f, runtime.Player.Combatant.Stats.AttackSpeed, 3);
        Assert.Equal(14, runtime.Player.Combatant.Stats.Vitality);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_IgnoresStatsForDifferentSession()
    {
        using ClientWorldSession runtime = CreateRuntime();
        PlayerCombatStats before = new()
        {
            Strength = runtime.Player.Combatant.BaseStats.Strength,
            AttackSpeed = runtime.Player.Combatant.BaseStats.AttackSpeed,
            Vitality = runtime.Player.Combatant.BaseStats.Vitality
        };
        AuthoritativeActionResult result = new(
            "other-session",
            21,
            46,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(new PlayerCombatStatsSnapshot(18, 3.5f, 14)));

        ClientAuthoritativeActionApplier.ApplyActionResult(
            runtime,
            new SceneEventHub(),
            result,
            runtime.LocalPlayerSessionId.Value);

        Assert.Equal(before.Strength, runtime.Player.Combatant.BaseStats.Strength);
        Assert.Equal(before.AttackSpeed, runtime.Player.Combatant.BaseStats.AttackSpeed, 3);
        Assert.Equal(before.Vitality, runtime.Player.Combatant.BaseStats.Vitality);
    }

    [Fact]
    public void ClientSnapshotDamageApplier_ApplyToRuntime_PublishesDamageEventForResolvedEnemy()
    {
        using ClientWorldSession runtime = CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(evt => observed.Add(evt));

        RemoteEnemyStateApplier.ApplyActorSnapshots(runtime.Enemies,
        [
            new ActorSnapshot(
                3001,
                CreatureKind.Enemy,
                null,
                new Vector3(6f, 1.5f, 6f),
                -Vector3.UnitZ,
                70,
                100,
                true)
        ]);

        ServerSnapshotEnvelope snapshot = new(
            77,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            [new DamageEventDelta(3001, CreatureKind.Enemy, 11, ActorDied: false, CurrentHealth: 59, MaxHealth: 100)]);

        ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, new ClientScenePresenter());

        DamageAppliedEvent damage = Assert.Single(observed);
        Assert.Equal(11, damage.Amount);
        Assert.Equal(3001, damage.Actor.Id);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_AppliesRejectedCorrectionImmediately()
    {
        using ClientWorldSession runtime = CreateRuntime();
        runtime.Player.SetFacing(-Vector3.UnitZ);
        AuthoritativeActionResult result = new(
            "session-a",
            23,
            50,
            Accepted: false,
            Payload: null,
            Correction: new ClientCorrectionDelta(
                new Vector3(8f, 1.5f, 7f),
                Vector3.UnitX),
            Rejection: new RejectedUseDelta("Rejected"));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result);

        Vector3 position = runtime.Player.GetWorldPosition();
        Assert.Equal(8f, position.X, 3);
        Assert.Equal(1.5f, position.Y, 3);
        Assert.Equal(7f, position.Z, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(-1f, runtime.Player.Camera.Front.Z, 3);
    }

    [Fact]
    public void ClientCorrectionApplier_ApplySnapshotToRuntime_DoesNotOverwriteLocalCameraFacing()
    {
        using ClientWorldSession runtime = CreateRuntime();
        runtime.Player.SetFacing(Vector3.UnitX);
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(
                    runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    runtime.LocalPlayerSessionId.Value,
                    new Vector3(7f, 1.5f, 4f),
                    -Vector3.UnitZ,
                    65,
                    120,
                    true)
            ],
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            snapshot,
            runtime.LocalPlayerSessionId.Value);

        Assert.Equal(1f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.Z, 3);
        Assert.Equal(7f, runtime.Player.GetWorldPosition().X, 3);
    }

    [Fact]
    public void ClientCorrectionApplier_ApplySnapshotToRuntime_AppliesAuthoritativeCombatStats()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ServerSnapshotEnvelope snapshot = new(
            34,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(
                    runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    runtime.LocalPlayerSessionId.Value,
                    new Vector3(5f, 1.5f, 5f),
                    -Vector3.UnitZ,
                    110,
                    110,
                    true,
                    PlayerInteractionMode.Combat,
                    new PlayerCombatStatsSnapshot(20, 4f, 12))
            ],
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            snapshot,
            runtime.LocalPlayerSessionId.Value);

        Assert.Equal(20, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(4f, runtime.Player.Combatant.Stats.AttackSpeed, 3);
        Assert.Equal(12, runtime.Player.Combatant.Stats.Vitality);
    }

    [Fact]
    public void ClientCorrectionApplier_ApplySnapshotToRuntime_UsesLocalActorInteractionMode()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ServerSnapshotEnvelope snapshot = new(
            35,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(
                    101,
                    CreatureKind.Player,
                    "remote-primary",
                    new Vector3(7f, 1.5f, 4f),
                    -Vector3.UnitZ,
                    100,
                    100,
                    true,
                    PlayerInteractionMode.Combat),
                new ActorSnapshot(
                    runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    runtime.LocalPlayerSessionId.Value,
                    new Vector3(5f, 1.5f, 5f),
                    -Vector3.UnitZ,
                    100,
                    100,
                    true,
                    PlayerInteractionMode.Building)
            ],
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            snapshot,
            runtime.LocalPlayerSessionId.Value);

        Assert.Equal(PlayerInteractionMode.Building, runtime.InteractionMode);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_PublishesRejectedActionEvent()
    {
        using ClientWorldSession runtime = CreateRuntime();
        SceneEventHub events = new();
        List<AuthoritativeActionRejectedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<AuthoritativeActionRejectedEvent>(evt => observed.Add(evt));
        AuthoritativeActionResult result = new(
            "session-a",
            24,
            51,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta("Build rejected", "occupied"));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, events, result);

        AuthoritativeActionRejectedEvent rejection = Assert.Single(observed);
        Assert.Equal("session-a", rejection.Result.SessionId);
        Assert.Equal((ulong)24, rejection.Result.ActionSequence);
        Assert.Equal("Build rejected", rejection.Result.Rejection?.Reason);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_RecordsDiagnosticsForRejectedAction()
    {
        using ClientWorldSession runtime = CreateRuntime();
        RecordingDiagnosticsSink diagnostics = new();
        AuthoritativeActionResult result = new(
            "session-a",
            25,
            52,
            Accepted: false,
            Payload: null,
            Correction: new ClientCorrectionDelta(new Vector3(9f, 1.5f, 8f), Vector3.UnitY),
            Rejection: new RejectedUseDelta("Out of range"));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result, diagnostics);

        Assert.Single(diagnostics.RecordedResults);
        Assert.True(diagnostics.CorrectionApplied);
        Assert.Equal("session-a", diagnostics.RecordedResults[0].SessionId);
        Assert.Equal((ulong)25, diagnostics.RecordedResults[0].ActionSequence);
    }

    internal static ClientWorldSession CreateRuntime()
    {
        return new ClientWorldSession(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
    }

    // The map shape WorldSessionRuntime builds: startup terrain and locally simulated cube bodies.
    internal static Map CreateSimulatedMap(PhysicsIntegrator physics)
    {
        return new Map(
            physics,
            generateStartupTerrain: true,
            width: 10,
            depth: 10);
    }

    internal static bool IntersectsCell(GeometryRenderBlockDescriptor block, GeometryCellKey cell)
    {
        Vector3 blockMin = block.Center - (block.Size * 0.5f);
        Vector3 blockMax = block.Center + (block.Size * 0.5f);
        Vector3 cellMin = new(cell.X, cell.Y, cell.Z);
        Vector3 cellMax = cellMin + Vector3.One;

        return blockMin.X < cellMax.X && blockMax.X > cellMin.X &&
            blockMin.Y < cellMax.Y && blockMax.Y > cellMin.Y &&
            blockMin.Z < cellMax.Z && blockMax.Z > cellMin.Z;
    }

    internal static void AssertVector3(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    internal static bool ApproximatelyEqual(Vector3 left, Vector3 right)
    {
        return MathF.Abs(left.X - right.X) < 0.001f &&
            MathF.Abs(left.Y - right.Y) < 0.001f &&
            MathF.Abs(left.Z - right.Z) < 0.001f;
    }

    internal sealed class FakeReplicatedPlayerTarget : IReplicatedPlayerTarget
    {
        public Vector3 Position { get; private set; }

        public Vector3 Facing { get; private set; }

        public int CurrentHealth { get; private set; }

        public int MaxHealth { get; private set; }

        public void SetWorldPosition(Vector3 position) => Position = position;

        public void SetFacing(Vector3 facing) => Facing = facing;

        public void SetHealth(int currentHealth, int maxHealth)
        {
            CurrentHealth = currentHealth;
            MaxHealth = maxHealth;
        }
    }

    internal sealed class FakeReplicatedSessionStateTarget : IReplicatedSessionStateTarget
    {
        public PlayerInteractionMode InteractionMode { get; private set; } = PlayerInteractionMode.Building;

        public void SetAuthoritativeInteractionMode(PlayerInteractionMode interactionMode)
        {
            InteractionMode = interactionMode;
        }
    }

    internal sealed class RecordingDiagnosticsSink : IMultiplayerDiagnosticsSink
    {
        public List<AuthoritativeActionResult> RecordedResults { get; } = [];

        public bool CorrectionApplied { get; private set; }

        public void RecordSnapshotApplied(ServerSnapshotEnvelope snapshot, string sessionId)
        {
        }

        public void RecordActionResult(AuthoritativeActionResult result, bool correctionApplied)
        {
            RecordedResults.Add(result);
            CorrectionApplied = correctionApplied;
        }
    }
}
