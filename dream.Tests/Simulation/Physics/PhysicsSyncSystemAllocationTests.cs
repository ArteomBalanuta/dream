using System.Globalization;
using System.Text;
using BulletSharp;
using BulletSharp.Math;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// PhysicsSyncSystem runs every client and server tick over every dynamic map body, most of
// which rest. Its own bookkeeping must not allocate in that steady state.
public class PhysicsSyncSystemAllocationTests
{
    private readonly ITestOutputHelper _output;

    public PhysicsSyncSystemAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SyncDynamicMapBodies_RestingCubes_DoesNotAllocate()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 16, depth: 16);
        for (int x = 1; x < 15; x += 2)
        {
            for (int z = 1; z < 15; z += 2)
            {
                map.AddCube(new TkVector3(x, 1f, z), withPhysics: true);
            }
        }

        SettleWorld(physics);
        PhysicsSyncSystem sync = new();
        List<RigidBody> bodies = physics.DynamicMapCubeBodies;
        Assert.True(bodies.Count >= 40);

        long bytes = AllocationAssert.AtMost(0, () => sync.SyncDynamicMapBodies(map, bodies));

        _output.WriteLine($"resting cubes: bodies={bodies.Count} bytesPerSync={bytes}");
    }

    [Fact]
    public void SyncDynamicMapBodies_RestingRubbleAndCubes_DoesNotAllocate()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 16, depth: 16);
        for (int x = 1; x < 15; x += 2)
        {
            for (int z = 1; z < 15; z += 2)
            {
                map.AddCube(new TkVector3(x, 1f, z), withPhysics: true);
            }
        }

        GeometryCellKey[] fracturedCells = [new(3, 1, 3), new(7, 1, 7), new(11, 1, 11)];
        foreach (GeometryCellKey cell in fracturedCells)
        {
            Assert.True(map.FractureCubeIntoPieces(cell));
        }

        SettleWorld(physics);
        PhysicsSyncSystem sync = new();
        List<RigidBody> bodies = physics.DynamicMapCubeBodies;
        sync.SyncDynamicMapBodies(map, bodies);

        // Map.UpdateFracturePiecePositions with a reused accepted-index set costs nothing for
        // resting pieces since MEM-9 (it was 1496 B per tick here before), so neither the map
        // updates on their own nor the whole sync may allocate.
        Dictionary<GeometryCellKey, List<(int PieceIndex, TkVector3 Center)>> updatesByCell = [];
        int rubbleBodyCount = 0;
        foreach (RigidBody body in bodies)
        {
            if (body.UserObject is GeometryDynamicBodyReference reference)
            {
                rubbleBodyCount++;
                body.MotionState.GetWorldTransform(out Matrix transform);
                if (!updatesByCell.TryGetValue(reference.CellKey, out List<(int PieceIndex, TkVector3 Center)>? updates))
                {
                    updates = [];
                    updatesByCell[reference.CellKey] = updates;
                }

                updates.Add((reference.PieceIndex, transform.Origin.ToOpenTK()));
            }
        }

        Assert.True(rubbleBodyCount > 0);
        Assert.Equal(fracturedCells.Length, updatesByCell.Count);
        HashSet<int> acceptedPieceIndices = [];
        long mapBytes = AllocationAssert.AtMost(0, () =>
        {
            foreach (KeyValuePair<GeometryCellKey, List<(int PieceIndex, TkVector3 Center)>> entry in updatesByCell)
            {
                Assert.True(map.UpdateFracturePiecePositions(entry.Key, entry.Value, acceptedPieceIndices));
            }
        });

        long syncBytes = AllocationAssert.AtMost(0, () => sync.SyncDynamicMapBodies(map, bodies));

        _output.WriteLine(
            $"resting rubble+cubes: bodies={bodies.Count} rubble={rubbleBodyCount} " +
            $"bytesPerSync={syncBytes} mapFractureUpdates={mapBytes}");
    }

    [Fact]
    public void SyncDynamicMapBodies_ScriptedPushScenario_MatchesRecordedGolden()
    {
        string trace = RunScriptedScenario();
        _output.WriteLine(trace);

        Assert.Equal(ScriptedScenarioGolden.ReplaceLineEndings("\n"), trace);
    }

    private static void SettleWorld(PhysicsIntegrator physics)
    {
        for (int step = 0; step < 120; step++)
        {
            physics.StepSimulationFixed(1f / 60f);
        }
    }

    // Golden recorded from the pre-MEM-2 implementation (LINQ grouping, per-call lists).
    private const string ScriptedScenarioGolden = """
        tick 0: cube2 (4,1,2)->(5,1,2)@(5.6,1.5,2.5) cube1 (3,1,2)->(4,1,2)@(4.6,1.5,2.5) cube0 (2,1,2)->(3,1,2)@(3.6,1.5,2.5) | blocked=(3.3,1.5,6.5)
        tick 1: | blocked=(3.3,1.5,6.5)
        tick 2: cube0 (3,1,2)->(3,1,2)@(3.8,1.5,2.5) | blocked=(3.3,1.5,6.5)
        tick 3: | blocked=(3.3,1.5,6.5)
        tick 4: | blocked=(3.3,1.5,6.5)
        tick 5: | blocked=(3.3,1.5,6.5)
        tick 6: | blocked=(3.3,1.5,6.5)
        tick 7: | blocked=(2.5,1.5,6.5)
        tick 8: | blocked=(2.5,1.5,6.5)
        tick 9: cube3 (2,1,6)->(2,1,6)@(2.7,1.5,6.5) | blocked=(2.7,1.5,6.5)
        tick 10: | blocked=(2.7,1.5,6.5)
        tick 11: | blocked=(2.7,1.5,6.5)
        row0 body=(3.8,1.5,2.5) user=cube0@(3.8,1.5,2.5)
        row1 body=(4.6,1.5,2.5) user=cube1@(4.6,1.5,2.5)
        row2 body=(5.6000004,1.5,2.5) user=cube2@(5.6000004,1.5,2.5)
        blocked body=(2.7,1.5,6.5) user=cube3@(2.7,1.5,6.5)
        sleeper body=(9.5,1.5,9.5) user=cube5@(9.5,1.5,9.5)
        swapA body=(6.5,1.5,4.5) user=cube6@(6.5,1.5,4.5)
        swapB body=(7.5,1.5,4.5) user=cube7@(7.5,1.5,4.5)
        piece0 body=(7.4,1.1,7.3500004) user=piece0[7,1,7]@(7.4,1.1,7.3500004)
        piece1 body=(7.6499996,1.1,7.3) user=piece1[7,1,7]@(7.6499996,1.1,7.3)
        piece2 body=(7.5,1.1,7.7) user=piece2[7,1,7]@(7.5,1.1,7.7)
        orphan body=(1.5,1.25,10.5) user=piece0[1,1,10]@(1.5,1.25,10.5)
        cube0 pos=(3.8,1.5,2.5) cell=3,1,2
        cube1 pos=(4.6,1.5,2.5) cell=4,1,2
        cube2 pos=(5.6000004,1.5,2.5) cell=5,1,2
        cube3 pos=(2.7,1.5,6.5) cell=2,1,6
        cube4 pos=(3.5,1.5,6.5) cell=3,1,6
        cube5 pos=(9.5,1.5,9.5) cell=9,1,9
        cube6 pos=(6.5,1.5,4.5) cell=6,1,4
        cube7 pos=(7.5,1.5,4.5) cell=7,1,4

        """;

    private static string RunScriptedScenario()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 12, depth: 12);
        TkVector3[] cubeCells =
        [
            new(2f, 1f, 2f), new(3f, 1f, 2f), new(4f, 1f, 2f),
            new(2f, 1f, 6f), new(3f, 1f, 6f),
            new(9f, 1f, 9f),
            new(6f, 1f, 4f), new(7f, 1f, 4f),
        ];
        foreach (TkVector3 cell in cubeCells)
        {
            map.AddCube(cell, withPhysics: false);
        }

        Cube[] cubes = cubeCells
            .Select(cell => Assert.IsType<Cube>(map.Cubes[(int)cell.X, (int)cell.Y, (int)cell.Z]))
            .ToArray();
        GeometryCellKey rubbleCell = new(7, 1, 7);
        GeometryFracturePiece[] pieces =
        [
            new(new TkVector3(7.3f, 1.1f, 7.3f), 0.2f),
            new(new TkVector3(7.7f, 1.1f, 7.3f), 0.2f),
            new(new TkVector3(7.5f, 1.1f, 7.7f), 0.2f),
        ];
        Assert.True(map.RestoreFracturePieces(rubbleCell, GeometryMaterialId.Stone, pieces, withPhysics: false));

        // Body order: the row is listed trailing-first so the trailing cubes need retries;
        // cube 4 is a static blocker with no body; cubes 6 and 7 try to swap.
        List<ScriptedBody> scripted =
        [
            ScriptedBody.ForCube("row0", cubes[0]),
            ScriptedBody.ForCube("row1", cubes[1]),
            ScriptedBody.ForCube("row2", cubes[2]),
            ScriptedBody.ForCube("blocked", cubes[3]),
            ScriptedBody.ForCube("sleeper", cubes[5]),
            ScriptedBody.ForCube("swapA", cubes[6]),
            ScriptedBody.ForCube("swapB", cubes[7]),
            ScriptedBody.ForPiece("piece0", rubbleCell, 0, pieces[0].Center),
            ScriptedBody.ForPiece("piece1", rubbleCell, 1, pieces[1].Center),
            ScriptedBody.ForPiece("piece2", rubbleCell, 2, pieces[2].Center),
            ScriptedBody.ForPiece("orphan", new GeometryCellKey(1, 1, 10), 0, new TkVector3(1.5f, 1.25f, 10.5f)),
        ];
        Dictionary<string, ScriptedBody> byName = scripted.ToDictionary(body => body.Name);
        Dictionary<Cube, int> cubeIndices = cubes
            .Select((cube, index) => (cube, index))
            .ToDictionary(entry => entry.cube, entry => entry.index);
        List<RigidBody> bodies = scripted.Select(body => body.Body).ToList();
        PhysicsSyncSystem sync = new();
        StringBuilder trace = new();

        for (int tick = 0; tick < 12; tick++)
        {
            switch (tick)
            {
                case 0:
                    byName["row0"].MoveTo(new TkVector3(3.6f, 1.5f, 2.5f));
                    byName["row1"].MoveTo(new TkVector3(4.6f, 1.5f, 2.5f));
                    byName["row2"].MoveTo(new TkVector3(5.6f, 1.5f, 2.5f));
                    byName["blocked"].MoveTo(new TkVector3(3.3f, 1.5f, 6.5f));
                    byName["swapA"].MoveTo(new TkVector3(7.4f, 1.5f, 4.5f));
                    byName["swapB"].MoveTo(new TkVector3(6.4f, 1.5f, 4.5f));
                    byName["piece0"].MoveTo(pieces[0].Center + new TkVector3(0.1f, 0f, 0.05f));
                    byName["piece2"].MoveTo(new TkVector3(float.NaN, 1.2f, 7.5f));
                    break;
                case 2:
                    byName["row0"].MoveTo(new TkVector3(3.8f, 1.5f, 2.5f));
                    byName["piece1"].MoveTo(pieces[1].Center + new TkVector3(-0.05f, 0f, 0f));
                    break;
                case 3:
                    byName["swapA"].MoveTo(new TkVector3(6.5f, 1.5f, 4.5f));
                    byName["swapB"].MoveTo(new TkVector3(7.5f, 1.5f, 4.5f));
                    break;
                case 5:
                    byName["row2"].MoveTo(new TkVector3(5.6000004f, 1.5f, 2.5f));
                    break;
                case 9:
                    byName["blocked"].MoveTo(new TkVector3(2.7f, 1.5f, 6.5f));
                    break;
            }

            PhysicsSyncResult result = sync.SyncDynamicMapBodies(map, bodies);
            trace.Append("tick ").Append(tick).Append(':');
            foreach (PhysicsCubeMoveDelta moved in result.MovedCubes)
            {
                trace.Append(" cube").Append(cubeIndices[moved.Cube])
                    .Append(' ').Append(Format(moved.SourceCellPosition))
                    .Append("->").Append(Format(moved.TargetCellPosition))
                    .Append('@').Append(Format(moved.CenterPosition));
            }

            byName["blocked"].Body.MotionState.GetWorldTransform(out Matrix blockedTransform);
            trace.Append(" | blocked=").Append(Format(blockedTransform.Origin.ToOpenTK())).Append('\n');
        }

        foreach (ScriptedBody body in scripted)
        {
            body.Body.MotionState.GetWorldTransform(out Matrix transform);
            trace.Append(body.Name).Append(" body=").Append(Format(transform.Origin.ToOpenTK()))
                .Append(" user=").Append(FormatUserObject(body.Body.UserObject, cubeIndices))
                .Append('\n');
        }

        for (int i = 0; i < cubes.Length; i++)
        {
            trace.Append("cube").Append(i).Append(" pos=").Append(Format(cubes[i].Position));
            trace.Append(map.TryGetCellForCube(cubes[i], out GeometryCellKey cell)
                ? $" cell={cell.X},{cell.Y},{cell.Z}"
                : " cell=none");
            trace.Append('\n');
        }

        return trace.ToString();
    }

    private static string FormatUserObject(object? userObject, Dictionary<Cube, int> cubeIndices)
    {
        return userObject switch
        {
            ValueTuple<Cube, TkVector3> cubeBody => $"cube{cubeIndices[cubeBody.Item1]}@{Format(cubeBody.Item2)}",
            GeometryDynamicBodyReference piece =>
                $"piece{piece.PieceIndex}[{piece.CellKey.X},{piece.CellKey.Y},{piece.CellKey.Z}]@{Format(piece.InitialCenter)}",
            null => "null",
            _ => userObject.GetType().Name,
        };
    }

    private static string Format(TkVector3 value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"({value.X:R},{value.Y:R},{value.Z:R})");
    }

    private sealed class ScriptedBody
    {
        private readonly ScriptedMotionState _motionState;

        private ScriptedBody(string name, TkVector3 center, object userObject)
        {
            Name = name;
            _motionState = new ScriptedMotionState(center.ToBullet());
            BoxShape shape = new(0.5f, 0.5f, 0.5f);
            RigidBodyConstructionInfo rbInfo = new(1f, _motionState, shape, BulletSharp.Math.Vector3.Zero);
            Body = new RigidBody(rbInfo)
            {
                UserObject = userObject
            };
        }

        public string Name { get; }

        public RigidBody Body { get; }

        public static ScriptedBody ForCube(string name, Cube cube)
        {
            return new ScriptedBody(name, cube.Position, (cube, cube.Position));
        }

        public static ScriptedBody ForPiece(string name, GeometryCellKey cell, int pieceIndex, TkVector3 center)
        {
            return new ScriptedBody(name, center, new GeometryDynamicBodyReference(cell, pieceIndex, center));
        }

        public void MoveTo(TkVector3 center)
        {
            Matrix transform = Matrix.Translation(center.ToBullet());
            _motionState.SetWorldTransform(ref transform);
        }
    }

    private sealed class ScriptedMotionState : MotionState
    {
        private Matrix _worldTransform;

        public ScriptedMotionState(BulletSharp.Math.Vector3 position)
        {
            _worldTransform = Matrix.Translation(position);
        }

        public override void GetWorldTransform(out Matrix worldTrans)
        {
            worldTrans = _worldTransform;
        }

        public override void SetWorldTransform(ref Matrix worldTrans)
        {
            _worldTransform = worldTrans;
        }
    }
}
