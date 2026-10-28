using BulletSharp;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Determinism;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// MEM-10 writes a moved cube's center into the body's existing boxed (Cube, Vector3) UserObject
// instead of boxing a new tuple (WorldPhysicsBinder.SetCompatibilityCubeCenter). That is only
// safe while every body owns its own box and every writer of a center goes through either that
// in-place write or a fresh box. MEM-2 (PhysicsSyncSystem) and MEM-9 (in-place descriptor
// rewrites, spatial index list reuse) write the same state. This runs them all together for a
// long scenario: a physics-backed server map pushes cube chains across cells and 16-cell spatial
// sectors and fractures cubes, and a ClientWorldSession mirror applies the server's move and
// fracture deltas (moving its kinematic bodies in place) while it simulates its own rubble.
// Every tick, on both worlds, every body's UserObject must name the map's current center, no two
// bodies may share a box, the map invariants must hold, and two identical runs must produce the
// same state checksums tick for tick.
public class InPlaceUserObjectPhysicsSyncIntegrationTests(ITestOutputHelper output)
{
    private const int MapSize = 40;
    private const int Ticks = 540;
    private const float FixedDelta = 1f / 60f;
    private const int ChainLength = 4;
    private const int PushEveryTicks = 12;
    private const int KickRubbleEveryTicks = 30;
    private static readonly int[] FractureTicks = [140, 260, 380];

    [Fact]
    public async Task LongPushFractureScenario_EveryBodyUserObjectMatchesTheMap_BoxesAreUnshared_AndRunsAreDeterministic()
    {
        // The two runs share nothing (each builds its own physics world, maps and seeded random),
        // so they run at the same time; determinism means they must still agree tick for tick.
        RunResult[] runs = await Task.WhenAll(Task.Run(Run), Task.Run(Run));
        RunResult first = runs[0];
        RunResult second = runs[1];

        Assert.Equal(first.ServerChecksums.Length, second.ServerChecksums.Length);
        for (int tick = 0; tick < first.ServerChecksums.Length; tick++)
        {
            Assert.True(
                first.ServerChecksums[tick] == second.ServerChecksums[tick],
                $"tick {tick}: server checksum differs between identical runs.");
            Assert.True(
                first.ClientChecksums[tick] == second.ClientChecksums[tick],
                $"tick {tick}: client checksum differs between identical runs.");
        }

        Assert.Equal(first.Stats, second.Stats);
        RunStats stats = first.Stats;
        output.WriteLine(
            $"ticks={Ticks} serverMoves={stats.ServerMoves} cellChanges={stats.CellChanges} sectorChanges={stats.SectorChanges} " +
            $"fractures={stats.Fractures} clientInPlaceChecks={stats.ClientCubeBodyChecks} rubbleChecks={stats.RubbleBodyChecks}");

        // The scenario really exercised what it claims to.
        Assert.True(stats.ServerMoves > 200, $"Only {stats.ServerMoves} cube moves.");
        Assert.True(stats.CellChanges >= ChainLength, $"Only {stats.CellChanges} cell changes.");
        Assert.True(stats.SectorChanges >= 1, "No cube crossed a spatial sector boundary.");
        Assert.Equal(FractureTicks.Length, stats.Fractures);
        Assert.True(stats.RubbleBodyChecks > 0);
    }

    // Found by the scenario above: PhysicsSyncSystem used to report a move only when the center
    // moved more than 1 mm since the previous tick, not since the last center it reported. A cube
    // creeping slower than 1 mm per tick changed the server map every tick without a single
    // CubeMoved delta, so the mirror drifted without bound and, once the creep crossed a cell
    // boundary, held the cube in the wrong cell (world checksum mismatch, and later deltas named
    // a source cell the mirror lacked).
    [Fact]
    public void SubMillimetreCreepAcrossACellBoundary_IsReportedAsAMove()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(4f, 1f, 4f), isStatic: false, withPhysics: true));
        RigidBody body = Assert.Single(
            physics.DynamicMapCubeBodies,
            candidate => candidate.UserObject is ValueTuple<Cube, Vector3> stored && ReferenceEquals(stored.Item1, cube));
        PhysicsSyncSystem sync = new();
        Vector3 start = cube.Position;
        int reported = 0;

        // 0.9 mm per tick along +X, from the cell center to past the x = 5 boundary.
        for (int tick = 1; tick <= 700; tick++)
        {
            Vector3 center = start + new Vector3(0.0009f * tick, 0f, 0f);
            BulletSharp.Math.Matrix transform = BulletSharp.Math.Matrix.Translation(center.ToBullet());
            body.WorldTransform = transform;
            body.MotionState.SetWorldTransform(ref transform);
            body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
            reported += sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies).MovedCubes.Count;
        }

        Assert.True(map.TryGetCellForCube(cube, out GeometryCellKey cell));
        Assert.Equal(new GeometryCellKey(5, 1, 4), cell);
        Assert.True(reported > 0, $"The cube moved {cube.Position.X - start.X:F3} and changed cell without a single reported move.");
    }

    private RunResult Run()
    {
        using PhysicsIntegrator serverPhysics = new();
        Map server = new(serverPhysics, generateStartupTerrain: true, width: MapSize, depth: MapSize);
        using ClientWorldSession client = new(MapSize, new Vector3(MapSize - 3.5f, 6f, MapSize - 3.5f), new SceneEventHub());
        PhysicsSyncSystem serverSync = new();

        // Two chains: one along +X across the x = 16 sector edge, one along +Z across z = 16.
        (int chainAZ, int chainAStartX) = FindOpenRun(server, client.Map, alongX: true);
        (int chainBX, int chainBStartZ) = FindOpenRun(server, client.Map, alongX: false);
        List<Cube> chainA = PlaceChain(server, client.Map, i => new Vector3(chainAStartX + i, 1f, chainAZ), GeometryMaterialId.Stone);
        List<Cube> chainB = PlaceChain(server, client.Map, i => new Vector3(chainBX, 1f, chainBStartZ + i), GeometryMaterialId.Dirt);
        List<Cube> tracked = [.. chainA, .. chainB];
        // What the client was last told about each server cube. A cube is reported only once it
        // has moved 1 mm from its last reported center or changed cell, so this, not the server's
        // current center, is what the mirror must hold.
        Dictionary<Cube, Vector3> replicated = new(ReferenceEqualityComparer.Instance);
        foreach (Cube cube in tracked)
        {
            replicated[cube] = cube.Position;
        }
        Random random = new(5150);
        ulong sequence = 100;
        RunStats stats = default;
        ulong[] serverChecksums = new ulong[Ticks];
        ulong[] clientChecksums = new ulong[Ticks];
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;

        for (int tick = 0; tick < Ticks; tick++)
        {
            if (tick % PushEveryTicks == 0)
            {
                // Push each chain's tail into the rest of the chain.
                PushTail(server, serverPhysics, chainA, new BulletSharp.Math.Vector3(7f, 0f, 0f));
                PushTail(server, serverPhysics, chainB, new BulletSharp.Math.Vector3(0f, 0f, 7f));
            }

            if (tick % KickRubbleEveryTicks == 0)
            {
                KickRubble(serverPhysics, random);
                KickRubble(client.Physics, random);
            }

            List<WorldMutationDelta> deltas = [];
            int fractureIndex = Array.IndexOf(FractureTicks, tick);
            if (fractureIndex >= 0)
            {
                // Fracture a live chain cube (alternating chains), wherever it has been pushed.
                List<Cube> chain = fractureIndex % 2 == 0 ? chainA : chainB;
                Cube victim = chain.First(cube => server.TryGetCellForCube(cube, out _));
                Assert.True(server.TryGetCellForCube(victim, out GeometryCellKey cell));
                GeometryMaterialId material = server.TryGetGeometryCellAtCell(cell.X, cell.Y, cell.Z)!.Definition.MaterialId;
                Assert.True(server.FractureCubeIntoPieces(cell), $"tick {tick}: fracture of {cell} failed.");
                GeometryCellRecord rubble = server.TryGetRubbleAtCell(cell)!;
                deltas.Add(WorldMutationDelta.Fractured(new Vector3(cell.X, cell.Y, cell.Z), material, rubble.FracturePieces!) with { Sequence = ++sequence });
                stats.Fractures++;
            }

            serverPhysics.StepSimulationFixed(FixedDelta);
            PhysicsSyncResult result = serverSync.SyncDynamicMapBodies(server, serverPhysics.DynamicMapCubeBodies);
            foreach (PhysicsCubeMoveDelta move in result.MovedCubes)
            {
                stats.ServerMoves++;
                if (move.SourceCellPosition != move.TargetCellPosition)
                {
                    stats.CellChanges++;
                    if (Sector(move.SourceCellPosition) != Sector(move.TargetCellPosition))
                    {
                        stats.SectorChanges++;
                    }
                }

                deltas.Add(WorldMutationDelta.Moved(move.SourceCellPosition, move.CenterPosition) with { Sequence = ++sequence });
                replicated[move.Cube] = move.CenterPosition;
            }

            RemoteWorldMutationApplier.ApplyWorldMutations(client.Map, deltas);
            client.AdvancePredictionTick(
                (ulong)tick + 1,
                FixedDelta,
                PlayerCommandFrame.CreateIdle(PlayerSessionId.LocalDefault, (ulong)tick + 1),
                tuning);

            CheckWorld("server", tick, server, serverPhysics, tracked, ref stats);
            CheckWorld("client", tick, client.Map, client.Physics, tracked: null, ref stats);

            // The client mirror holds every live server cube at the center last replicated for it.
            AssertMirrorMatches(tick, server, client.Map, tracked, replicated);

            serverChecksums[tick] = SimulationStateChecksum.Compute(server, Array.Empty<ActorSnapshot>(), 0);
            clientChecksums[tick] = SimulationStateChecksum.Compute(client.Map, Array.Empty<ActorSnapshot>(), 0);
        }

        return new RunResult(serverChecksums, clientChecksums, stats);
    }

    // Every body in the Bullet world, not only the map's lists, so a stale body is caught too.
    private static void CheckWorld(string side, int tick, Map map, PhysicsIntegrator physics, List<Cube>? tracked, ref RunStats stats)
    {
        Assert.Empty(map.FindInvariantViolations());

        HashSet<object> boxes = new(ReferenceEqualityComparer.Instance);
        Dictionary<Cube, int> bodiesPerCube = new(ReferenceEqualityComparer.Instance);
        AlignedCollisionObjectArray objects = physics.World.CollisionObjectArray;
        for (int i = 0; i < objects.Count; i++)
        {
            CollisionObject body = objects[i];
            object? userObject = body.UserObject;
            if (userObject is null)
            {
                continue;
            }

            Assert.True(
                boxes.Add(userObject),
                $"{side} tick {tick}: two bodies share the UserObject instance {userObject}.");

            if (userObject is ValueTuple<Cube, Vector3> cubeBody)
            {
                Cube cube = cubeBody.Item1;
                Assert.True(
                    map.TryGetCellForCube(cube, out _),
                    $"{side} tick {tick}: a body still names a cube the map no longer has (stored center {cubeBody.Item2}).");
                Assert.True(
                    cubeBody.Item2 == cube.Position,
                    $"{side} tick {tick}: body UserObject center {cubeBody.Item2} != map cube center {cube.Position}.");
                bodiesPerCube[cube] = bodiesPerCube.GetValueOrDefault(cube) + 1;

                if ((body.CollisionFlags & CollisionFlags.KinematicObject) != 0)
                {
                    // A replicated (kinematic) body is moved to the center it stores.
                    Vector3 origin = body.WorldTransform.Origin.ToOpenTK();
                    Assert.True(
                        origin == cube.Position,
                        $"{side} tick {tick}: kinematic body at {origin}, cube center {cube.Position}.");
                    stats.ClientCubeBodyChecks++;
                }
            }
            else if (userObject is GeometryDynamicBodyReference reference)
            {
                GeometryCellRecord? rubble = map.TryGetRubbleAtCell(reference.CellKey);
                Assert.True(rubble?.FracturePieces != null, $"{side} tick {tick}: rubble body for {reference.CellKey} has no rubble.");
                Assert.InRange(reference.PieceIndex, 0, rubble!.FracturePieces!.Count - 1);
                Vector3 pieceCenter = rubble.FracturePieces[reference.PieceIndex].Center;
                Assert.True(
                    reference.InitialCenter == pieceCenter,
                    $"{side} tick {tick}: rubble body {reference.CellKey}#{reference.PieceIndex} stores {reference.InitialCenter}, piece is at {pieceCenter}.");
                stats.RubbleBodyChecks++;
            }
        }

        foreach (Cube cube in tracked ?? [])
        {
            if (map.TryGetCellForCube(cube, out _))
            {
                Assert.True(
                    bodiesPerCube.GetValueOrDefault(cube) == 1,
                    $"{side} tick {tick}: cube at {cube.Position} has {bodiesPerCube.GetValueOrDefault(cube)} bodies.");
            }
        }

        foreach ((Cube cube, int count) in bodiesPerCube)
        {
            Assert.True(count == 1, $"{side} tick {tick}: cube at {cube.Position} has {count} bodies.");
        }
    }

    private static void AssertMirrorMatches(int tick, Map server, Map client, List<Cube> tracked, Dictionary<Cube, Vector3> replicated)
    {
        foreach (Cube serverCube in tracked)
        {
            if (!server.TryGetCellForCube(serverCube, out _))
            {
                continue;
            }

            Vector3 center = replicated[serverCube];
            Cube? clientCube = client.TryGetCubeAtCell(
                (int)MathF.Floor(center.X + 1e-6f),
                (int)MathF.Floor(center.Y + 1e-6f),
                (int)MathF.Floor(center.Z + 1e-6f));
            Assert.True(clientCube != null, $"tick {tick}: client has no cube at the replicated center {center}.");
            Assert.True(
                clientCube!.Position == center,
                $"tick {tick}: client cube at {clientCube.Position}, last replicated center {center}.");
        }
    }

    private static List<Cube> PlaceChain(Map server, Map client, Func<int, Vector3> cellOf, GeometryMaterialId material)
    {
        List<Cube> chain = [];
        List<WorldMutationDelta> placed = [];
        for (int i = 0; i < ChainLength; i++)
        {
            Vector3 cell = cellOf(i);
            Cube cube = Assert.IsType<Cube>(server.TryAddCube(cell, isStatic: false, withPhysics: true, materialId: material));
            chain.Add(cube);
            placed.Add(new WorldMutationDelta(WorldMutationKind.CubePlaced, cell, MaterialId: material));
        }

        RemoteWorldMutationApplier.ApplyWorldMutations(client, placed);
        for (int i = 0; i < ChainLength; i++)
        {
            Vector3 cell = cellOf(i);
            Assert.NotNull(client.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z));
        }

        return chain;
    }

    // A run of free cells at y = 1..2 on both maps, straddling the 16-cell sector edge, with room
    // ahead for the chain to be pushed.
    private static (int Lane, int Start) FindOpenRun(Map server, Map client, bool alongX)
    {
        const int Start = 12;
        const int End = 24;
        for (int lane = 3; lane < MapSize - 3; lane++)
        {
            bool open = true;
            for (int along = Start; along <= End && open; along++)
            {
                for (int y = 1; y <= 2 && open; y++)
                {
                    (int x, int z) = alongX ? (along, lane) : (lane, along);
                    open = server.TryGetGeometryCellAtCell(x, y, z) == null &&
                        client.TryGetGeometryCellAtCell(x, y, z) == null &&
                        server.TryGetGeometryCellAtCell(x, 0, z) != null;
                }
            }

            // Keep the two chains apart.
            if (open && (alongX ? lane < 12 || lane > 26 : lane < 8 || lane > 26))
            {
                return (lane, Start);
            }
        }

        throw new InvalidOperationException($"No open run along {(alongX ? "X" : "Z")} on the generated terrain.");
    }

    private static void PushTail(Map map, PhysicsIntegrator physics, List<Cube> chain, BulletSharp.Math.Vector3 velocity)
    {
        Cube? tail = chain.FirstOrDefault(cube => map.TryGetCellForCube(cube, out _));
        if (tail == null)
        {
            return;
        }

        foreach (RigidBody body in physics.DynamicMapCubeBodies)
        {
            if (body.UserObject is ValueTuple<Cube, Vector3> cubeBody && ReferenceEquals(cubeBody.Item1, tail))
            {
                body.Activate(true);
                body.LinearVelocity = velocity;
                return;
            }
        }
    }

    private static void KickRubble(PhysicsIntegrator physics, Random random)
    {
        foreach (RigidBody body in physics.DynamicMapCubeBodies)
        {
            if (body.UserObject is not GeometryDynamicBodyReference)
            {
                continue;
            }

            body.Activate(true);
            body.LinearVelocity = new BulletSharp.Math.Vector3(
                (float)(random.NextDouble() - 0.5) * 3f,
                2.5f,
                (float)(random.NextDouble() - 0.5) * 3f);
        }
    }

    private static (int X, int Z) Sector(Vector3 cell)
    {
        return ((int)MathF.Floor(cell.X / 16f), (int)MathF.Floor(cell.Z / 16f));
    }

    private record struct RunStats(
        int ServerMoves,
        int CellChanges,
        int SectorChanges,
        int Fractures,
        int ClientCubeBodyChecks,
        int RubbleBodyChecks);

    private sealed record RunResult(ulong[] ServerChecksums, ulong[] ClientChecksums, RunStats Stats);
}
