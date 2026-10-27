using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BulletSharp;
using BulletSharp.Math;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.World;

// Map's per-tick physics-sync entry points, UpdateCubePosition and UpdateFracturePiecePositions,
// run for every dynamic body on every client and server tick. Bytes per call, Debug, before MEM-9,
// after MEM-9 and, with physics, after MEM-10 (WorldPhysicsBinder and WorldSpatialIndex):
//   UpdateFracturePiecePositions, 3 cells of resting rubble              1496 -> 0
//   UpdateFracturePiecePositions, 1 cell, 5 pieces moving                2184 -> 104 (the piece copy)
//   UpdateCubePosition, map without physics, same cell, sync on or off      0 -> 0
//   UpdateCubePosition, map without physics, cell change, sync on or off  280 -> 64 (the moved record)
//   UpdateCubePosition, map with physics, same cell, sync on              416 -> 200 -> 0
//   UpdateCubePosition, map with physics, same cell, sync off               0 -> 0
//   UpdateCubePosition, map with physics, cell change, sync on            832 -> 616 -> 64 (the moved record)
//   UpdateCubePosition, map with physics, cell change, sync off           656 -> 440 -> 64 (the moved record)
// WorldPhysicsBinder's own move and reindex of the cube's body is also measured on its own and
// must allocate nothing.
public class MapSyncAllocationTests
{
    // A GeometryCellRecord on 64-bit. The record keyed by the new cell is the one allocation a
    // cell change keeps.
    private const long MovedRecordBytes = 64;

    // Recorded on the pre-MEM-9 Map; see RunEquivalenceScenario.
    private const string EquivalenceGoldenHash = "CCF3773A256A7FD9035BFADA7BE12056B09A27CB1A89D7B8FA2BCB68AE848D4D";

    private readonly ITestOutputHelper _output;

    public MapSyncAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void UpdateFracturePiecePositions_RestingRubble_DoesNotAllocate()
    {
        Map map = new(physics: null, width: 16, depth: 16);
        List<(GeometryCellKey Cell, List<(int PieceIndex, Vector3 Center)> Updates)> cells = [];
        foreach (GeometryCellKey cell in new GeometryCellKey[] { new(3, 1, 3), new(7, 1, 7), new(11, 1, 11) })
        {
            Assert.True(map.AddCube(new Vector3(cell.X, cell.Y, cell.Z), withPhysics: false));
            Assert.True(map.FractureCubeIntoPieces(cell));
            cells.Add((cell, CurrentCenters(map, cell, Vector3.Zero)));
        }

        long withoutSet = AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < cells.Count; i++)
            {
                Assert.True(map.UpdateFracturePiecePositions(cells[i].Cell, cells[i].Updates));
            }
        });

        HashSet<int> accepted = [];
        long withSet = AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < cells.Count; i++)
            {
                Assert.True(map.UpdateFracturePiecePositions(cells[i].Cell, cells[i].Updates, accepted));
                // Assert.True: Assert.Equal's comparer allocates inside the measured window.
                Assert.True(accepted.Count == cells[i].Updates.Count);
            }
        });

        _output.WriteLine($"resting rubble, 3 cells: {withoutSet} B; with a reused accepted set: {withSet} B");
    }

    [Fact]
    public void UpdateFracturePiecePositions_MovingRubble_AllocatesOnlyThePieceCopy()
    {
        Map map = new(physics: null, width: 16, depth: 16);
        GeometryCellKey cell = new(5, 1, 5);
        Assert.True(map.AddCube(new Vector3(cell.X, cell.Y, cell.Z), withPhysics: false));
        Assert.True(map.FractureCubeIntoPieces(cell));
        List<(int PieceIndex, Vector3 Center)> even = CurrentCenters(map, cell, Vector3.Zero);
        List<(int PieceIndex, Vector3 Center)> odd = CurrentCenters(map, cell, new Vector3(0.01f, 0f, 0f));
        HashSet<int> accepted = [];
        int toggle = 0;

        // The piece list is published through RubbleCells, so a move copies it: an array header
        // plus 16 B per piece. The render and collision descriptors are rewritten in place.
        long pieceCopyBytes = 24 + (16 * even.Count);
        long bytes = AllocationAssert.AtMost(
            pieceCopyBytes,
            () => map.UpdateFracturePiecePositions(cell, (toggle++ & 1) == 0 ? odd : even, accepted));

        _output.WriteLine($"moving rubble, {even.Count} pieces: {bytes} B per call (piece copy {pieceCopyBytes} B)");
        Assert.Equal(even.Count, map.CaptureFullGeometryRenderExport().DynamicBlocks.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdateCubePosition_SameCellWithoutPhysics_DoesNotAllocate(bool syncPhysics)
    {
        long bytes = MeasureCubeMove(withPhysics: false, syncPhysics, crossCell: false, budget: 0);
        _output.WriteLine($"same cell, no physics, sync={syncPhysics}: {bytes} B");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdateCubePosition_CellChangeWithoutPhysics_AllocatesOnlyTheMovedRecord(bool syncPhysics)
    {
        long bytes = MeasureCubeMove(withPhysics: false, syncPhysics, crossCell: true, budget: MovedRecordBytes);
        _output.WriteLine($"cell change, no physics, sync={syncPhysics}: {bytes} B");
    }

    [Fact]
    public void UpdateCubePosition_SameCellWithPhysicsUnsynced_DoesNotAllocate()
    {
        long bytes = MeasureCubeMove(withPhysics: true, syncPhysics: false, crossCell: false, budget: 0);
        _output.WriteLine($"same cell, physics, unsynced: {bytes} B");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void UpdateCubePosition_WithPhysics_AllocatesNoMoreThanTheBinder(bool crossCell, bool syncPhysics)
    {
        long binderBytes = MeasureBinderMove(crossCell, syncPhysics);
        Assert.True(binderBytes == 0, $"WorldPhysicsBinder allocated {binderBytes} B per move; budget is 0 B.");
        long budget = crossCell ? MovedRecordBytes : 0;
        long bytes = MeasureCubeMove(withPhysics: true, syncPhysics, crossCell, budget);
        _output.WriteLine($"physics, crossCell={crossCell}, sync={syncPhysics}: {bytes} B; binder alone {binderBytes} B");
    }

    [Fact]
    public void ScriptedMovesAndRubble_MatchPreMem9Map()
    {
        string trace = RunEquivalenceScenario();
        string hash = Hash(trace);
        if (hash != EquivalenceGoldenHash)
        {
            _output.WriteLine(trace);
        }

        Assert.Equal(EquivalenceGoldenHash, hash);
    }

    private static List<(int PieceIndex, Vector3 Center)> CurrentCenters(Map map, GeometryCellKey cell, Vector3 offset)
    {
        IReadOnlyList<GeometryFracturePiece> pieces = map.TryGetRubbleAtCell(cell)!.FracturePieces!;
        return pieces.Select((piece, index) => (index, piece.Center + offset)).ToList();
    }

    private static long MeasureCubeMove(bool withPhysics, bool syncPhysics, bool crossCell, long budget)
    {
        using PhysicsIntegrator? physics = withPhysics ? new PhysicsIntegrator() : null;
        Map map = new(physics, width: 10, depth: 10);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: withPhysics));
        (Vector3 even, Vector3 odd) = MovePositions(crossCell);
        int toggle = 0;
        long bytes = AllocationAssert.AtMost(
            budget,
            () => map.UpdateCubePosition(cube, (toggle++ & 1) == 0 ? odd : even, syncPhysics));
        Assert.Empty(map.FindInvariantViolations());
        Assert.True(map.TryGetCellForCube(cube, out GeometryCellKey cell));
        Assert.Same(cube, map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
        return bytes;
    }

    // The same body moved through WorldPhysicsBinder alone, as UpdateCubePosition drives it: a
    // synced move moves and reindexes the body, an unsynced cell change only reindexes it.
    private static long MeasureBinderMove(bool crossCell, bool syncPhysics)
    {
        using PhysicsIntegrator physics = new();
        WorldPhysicsBinder binder = new(physics);
        Map cubeSource = new(physics: null, width: 10, depth: 10);
        Cube cube = Assert.IsType<Cube>(cubeSource.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: false));
        (Vector3 even, Vector3 odd) = MovePositions(crossCell);
        GeometryCellKey evenKey = new(3, 2, 3);
        GeometryCellKey oddKey = crossCell ? new GeometryCellKey(4, 2, 3) : evenKey;
        binder.SyncCellCollisionBodies(
            evenKey,
            [
                new GeometryCollisionBodyDescriptor(
                    GeometryCollisionBodyKind.Cell,
                    cube.Position,
                    new Vector3(0.5f),
                    IsStatic: false,
                    CellKey: evenKey,
                    CompatibilityCube: cube)
            ]);
        int toggle = 0;
        return AllocationAssert.MeasureBytesPerIteration(() =>
        {
            bool toOdd = (toggle++ & 1) == 0;
            GeometryCellKey from = toOdd ? evenKey : oddKey;
            GeometryCellKey to = toOdd ? oddKey : evenKey;
            if (syncPhysics)
            {
                Assert.True(binder.TryMoveCompatibilityCubeBody(cube, from, to, toOdd ? odd : even));
            }
            else
            {
                binder.ReindexCompatibilityCubeBody(cube, from, to);
            }
        });
    }

    private static (Vector3 Even, Vector3 Odd) MovePositions(bool crossCell)
    {
        return (new Vector3(3.4f, 2.5f, 3.5f), crossCell ? new Vector3(4.6f, 2.5f, 3.5f) : new Vector3(3.6f, 2.5f, 3.5f));
    }

    private static string Hash(string trace)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace)));
    }

    // A scripted sequence over every UpdateCubePosition and UpdateFracturePiecePositions branch.
    // After each step it records the result, whether StaticContentVersion moved, the invariant
    // check, and the full map state: geometry and rubble records, the presentation, full render
    // and collision exports (in enumeration order) and the binder's bodies, so any change in
    // content or order shows up. EquivalenceGoldenHash is this trace's hash on the pre-MEM-9 Map.
    private static string RunEquivalenceScenario()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 12, depth: 12);
        Dictionary<Cube, string> names = new(ReferenceEqualityComparer.Instance);
        Cube Add(string name, Vector3 cell, bool isStatic, bool withPhysics)
        {
            Cube cube = Assert.IsType<Cube>(map.TryAddCube(cell, isStatic: isStatic, withPhysics: withPhysics));
            names[cube] = name;
            return cube;
        }

        Cube a = Add("A", new Vector3(2f, 1f, 2f), isStatic: false, withPhysics: true);
        Cube b = Add("B", new Vector3(5f, 1f, 5f), isStatic: false, withPhysics: true);
        Cube c = Add("C", new Vector3(8f, 1f, 8f), isStatic: true, withPhysics: true);
        Cube e = Add("E", new Vector3(5f, 1f, 6f), isStatic: false, withPhysics: false);
        Add("D", new Vector3(6f, 1f, 2f), isStatic: false, withPhysics: true);
        GeometryCellKey fractured = new(6, 1, 2);
        Assert.True(map.FractureCubeIntoPieces(fractured));
        GeometryCellKey restored = new(9, 1, 3);
        Assert.True(map.RestoreFracturePieces(
            restored,
            GeometryMaterialId.Stone,
            [
                new GeometryFracturePiece(new Vector3(9.3f, 1.1f, 3.3f), 0.2f),
                new GeometryFracturePiece(new Vector3(9.7f, 1.1f, 3.3f), 0.2f),
                new GeometryFracturePiece(new Vector3(9.5f, 1.1f, 3.7f), 0.2f),
            ],
            withPhysics: false));

        StringBuilder trace = new();
        AppendState(trace, map, names);
        AppendPhysics(trace, physics, names);
        Vector3 nan = new(float.NaN, 1.2f, 3.5f);
        Vector3 restoredPiece0 = new(9.3f, 1.1f, 3.3f);
        Vector3 restoredPiece1 = new(9.7f, 1.1f, 3.3f);
        Vector3 fracturedPiece0 = map.TryGetRubbleAtCell(fractured)!.FracturePieces![0].Center;
        (string Name, Func<string> Action)[] steps =
        [
            ("A same-cell sync", () => Run(() => map.UpdateCubePosition(a, new Vector3(2.6f, 1.5f, 2.4f), syncPhysics: true))),
            ("A same-cell nosync", () => Run(() => map.UpdateCubePosition(a, new Vector3(2.7f, 1.5f, 2.4f), syncPhysics: false))),
            ("A same-cell unchanged sync", () => Run(() => map.UpdateCubePosition(a, new Vector3(2.7f, 1.5f, 2.4f), syncPhysics: true))),
            ("A cross-cell sync", () => Run(() => map.UpdateCubePosition(a, new Vector3(3.2f, 1.5f, 2.5f), syncPhysics: true))),
            ("A cross-cell nosync", () => Run(() => map.UpdateCubePosition(a, new Vector3(4.1f, 1.5f, 2.5f), syncPhysics: false))),
            ("A same-cell sync after nosync", () => Run(() => map.UpdateCubePosition(a, new Vector3(4.3f, 1.5f, 2.5f), syncPhysics: true))),
            ("A cross-cell back sync", () => Run(() => map.UpdateCubePosition(a, new Vector3(2.5f, 1.5f, 2.5f), syncPhysics: true))),
            ("B unchanged sync", () => Run(() => map.UpdateCubePosition(b, b.Position, syncPhysics: true))),
            ("B into E blocked", () => Run(() => map.UpdateCubePosition(b, new Vector3(5.5f, 1.5f, 6.5f), syncPhysics: true))),
            ("B out of bounds", () => Run(() => map.UpdateCubePosition(b, new Vector3(-0.5f, 1.5f, 5.5f), syncPhysics: true))),
            ("B NaN", () => Run(() => map.UpdateCubePosition(b, nan, syncPhysics: true))),
            ("E bodyless same-cell sync", () => Run(() => map.UpdateCubePosition(e, new Vector3(5.4f, 1.5f, 6.5f), syncPhysics: true))),
            ("E bodyless cross-cell sync", () => Run(() => map.UpdateCubePosition(e, new Vector3(4.6f, 1.5f, 6.5f), syncPhysics: true))),
            ("E cross-cell nosync", () => Run(() => map.UpdateCubePosition(e, new Vector3(3.6f, 1.5f, 6.5f), syncPhysics: false))),
            ("C static same-cell nosync", () => Run(() => map.UpdateCubePosition(c, new Vector3(8.6f, 1.5f, 8.5f), syncPhysics: false))),
            ("C static same-cell sync", () => Run(() => map.UpdateCubePosition(c, new Vector3(8.4f, 1.5f, 8.5f), syncPhysics: true))),
            ("C static unchanged nosync", () => Run(() => map.UpdateCubePosition(c, new Vector3(8.4f, 1.5f, 8.5f), syncPhysics: false))),
            ("C static cross-cell sync", () => Run(() => map.UpdateCubePosition(c, new Vector3(9.5f, 1.5f, 8.5f), syncPhysics: true))),
            ("C now-dynamic cross-cell sync", () => Run(() => map.UpdateCubePosition(c, new Vector3(10.5f, 1.5f, 8.5f), syncPhysics: true))),
            ("rubble resting", () => Rubble(map, restored, [(0, restoredPiece0), (1, restoredPiece1)])),
            ("rubble move one", () => Rubble(map, restored, [(0, restoredPiece0 + new Vector3(0.05f, 0f, 0f)), (1, restoredPiece1)])),
            ("rubble duplicate index", () => Rubble(map, restored, [(1, restoredPiece1 + new Vector3(0f, 0f, 0.05f)), (1, restoredPiece1)])),
            ("rubble invalid and out of range", () => Rubble(map, restored, [(2, nan), (7, restoredPiece0), (-1, restoredPiece0)])),
            ("rubble mixed", () => Rubble(map, restored, [(2, nan), (0, restoredPiece0)])),
            ("rubble empty", () => Rubble(map, restored, [])),
            ("rubble missing cell", () => Rubble(map, new GeometryCellKey(1, 1, 1), [(0, restoredPiece0)])),
            ("fractured rubble move", () => Rubble(map, fractured, [(0, fracturedPiece0 + new Vector3(0f, -0.05f, 0f))])),
            ("rubble two-arg", () => Bool(map.UpdateFracturePiecePositions(restored, [(2, new Vector3(9.5f, 1.15f, 3.7f))]))),
            ("rubble single", () => Bool(map.UpdateFracturePiecePosition(restored, 2, new Vector3(9.5f, 1.2f, 3.7f)))),
            ("A remove", () => Bool(map.RemoveCube(a))),
            ("rubble remove", () => Bool(map.RemoveRubble(restored))),
        ];

        foreach ((string name, Func<string> action) in steps)
        {
            long versionBefore = map.StaticContentVersion;
            string result = action();
            trace.Append("== ").Append(name).Append(": ").Append(result)
                .Append(" staticVersionChanged=").Append(Bool(map.StaticContentVersion != versionBefore))
                .Append('\n');
            AppendState(trace, map, names);
            AppendPhysics(trace, physics, names);
        }

        return trace.ToString();
    }

    private static string Rubble(Map map, GeometryCellKey cell, (int PieceIndex, Vector3 Center)[] updates)
    {
        bool accepted = map.UpdateFracturePiecePositions(cell, updates, out HashSet<int> acceptedIndices);
        return $"{Bool(accepted)} accepted=[{string.Join(",", acceptedIndices.Order())}]";
    }

    private static string Run(Action action)
    {
        action();
        return "done";
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    private static void AppendState(StringBuilder trace, Map map, Dictionary<Cube, string> names)
    {
        foreach (string violation in map.FindInvariantViolations())
        {
            trace.Append("violation ").Append(violation).Append('\n');
        }

        foreach ((GeometryCellKey key, GeometryCellRecord record) in map.GeometryCells)
        {
            if (key.Y == 0)
            {
                continue;
            }

            Cube? cube = map.TryGetCubeAtCell(key.X, key.Y, key.Z);
            trace.Append("cell ").Append(Format(key)).Append(" record=").Append(Format(record.Key))
                .Append(" cube=").Append(FormatCube(cube, names)).Append('\n');
        }

        foreach ((GeometryCellKey key, GeometryCellRecord record) in map.RubbleCells)
        {
            trace.Append("rubble ").Append(Format(key));
            foreach (GeometryFracturePiece piece in record.FracturePieces!)
            {
                trace.Append(' ').Append(Format(piece.Center)).Append('/').Append(Format(piece.SideLength));
            }

            trace.Append('\n');
        }

        GeometryRenderExport presentation = map.CapturePresentationGeometryRenderExport(new Vector3(6f, 1.5f, 6f), 100f);
        trace.Append("presentation versionMatches=").Append(Bool(presentation.StaticContentVersion == map.StaticContentVersion)).Append('\n');
        AppendRenderExport(trace, presentation, names);
        AppendRenderExport(trace, map.CaptureFullGeometryRenderExport(), names);

        GeometryCollisionExport collision = map.CaptureGeometryCollisionExport();
        trace.Append("collision dirty=").Append(FormatKeys(collision.DirtyCells)).Append('\n');
        foreach (GeometryCollisionBodyDescriptor body in collision.Bodies)
        {
            trace.Append("  body ").Append(body.Kind).Append(' ').Append(Format(body.Center))
                .Append(' ').Append(Format(body.HalfExtents)).Append(" static=").Append(Bool(body.IsStatic))
                .Append(" cell=").Append(body.CellKey is { } cell ? Format(cell) : "-")
                .Append(" sub=").Append(body.SubcellKey?.ToString() ?? "-")
                .Append(" cube=").Append(FormatCube(body.CompatibilityCube, names))
                .Append(" piece=").Append(body.FracturePieceIndex?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append('\n');
        }
    }

    // The bodies the map's physics binder created, moved or removed, in list order.
    private static void AppendPhysics(StringBuilder trace, PhysicsIntegrator physics, Dictionary<Cube, string> names)
    {
        trace.Append("physics bodies=").Append(physics.MapCubeBodies.Count)
            .Append(" dynamic=").Append(physics.DynamicMapCubeBodies.Count).Append('\n');
        foreach (RigidBody body in physics.MapCubeBodies)
        {
            body.MotionState.GetWorldTransform(out Matrix transform);
            string user = body.UserObject switch
            {
                ValueTuple<Cube, Vector3> cubeBody => FormatCube(cubeBody.Item1, names) + "/" + Format(cubeBody.Item2),
                GeometryDynamicBodyReference piece => $"piece{piece.PieceIndex}{Format(piece.CellKey)}/{Format(piece.InitialCenter)}",
                null => "-",
                _ => body.UserObject.GetType().Name,
            };
            trace.Append("  ").Append(Format(transform.Origin.ToOpenTK()))
                .Append(" world=").Append(Format(body.WorldTransform.Origin.ToOpenTK()))
                .Append(" mass=").Append(Format(body.InvMass))
                .Append(" dynamic=").Append(Bool(physics.DynamicMapCubeBodies.Contains(body)))
                .Append(" user=").Append(user).Append('\n');
        }
    }

    private static void AppendRenderExport(StringBuilder trace, GeometryRenderExport export, Dictionary<Cube, string> names)
    {
        trace.Append("render dirty=").Append(FormatKeys(export.DirtyCells))
            .Append(" staticDirty=").Append(FormatKeys(export.StaticDirtyCells)).Append('\n');
        trace.Append("  cells");
        foreach (GeometryRenderCell cell in export.Cells)
        {
            if (cell.Key.Y == 0)
            {
                continue;
            }

            trace.Append(' ').Append(Format(cell.Key)).Append('=').Append(FormatCube(cell.CompatibilityCube, names));
        }

        trace.Append('\n');
        AppendCubes(trace, "  compat", export.CompatibilityCubes, names);
        AppendCubes(trace, "  dynamicCompat", export.DynamicCompatibilityCubes, names);
        AppendBlocks(trace, "  blocks", export.Blocks);
        AppendBlocks(trace, "  dynamicBlocks", export.DynamicBlocks);
    }

    private static void AppendCubes(StringBuilder trace, string label, IReadOnlyList<Cube> cubes, Dictionary<Cube, string> names)
    {
        trace.Append(label);
        foreach (Cube cube in cubes)
        {
            if (!names.ContainsKey(cube))
            {
                continue;
            }

            trace.Append(' ').Append(FormatCube(cube, names));
        }

        trace.Append(" total=").Append(cubes.Count).Append('\n');
    }

    private static void AppendBlocks(StringBuilder trace, string label, IReadOnlyList<GeometryRenderBlockDescriptor> blocks)
    {
        trace.Append(label);
        foreach (GeometryRenderBlockDescriptor block in blocks)
        {
            trace.Append(' ').Append(Format(block.CellKey)).Append(Format(block.Center))
                .Append('/').Append(Format(block.SideLength)).Append('/').Append(block.MaterialId)
                .Append('/').Append(block.ShaderName).Append('/').Append(block.SubcellKey?.ToString() ?? "-")
                .Append('/').Append(Format(block.VisualFillScale)).Append('/').Append(Format(block.SizeOverride));
        }

        trace.Append('\n');
    }

    private static string FormatCube(Cube? cube, Dictionary<Cube, string> names)
    {
        if (cube == null)
        {
            return "-";
        }

        return (names.TryGetValue(cube, out string? name) ? name : "?") + "@" + Format(cube.Position);
    }

    private static string FormatKeys(IReadOnlyList<GeometryCellKey> keys)
    {
        return "[" + string.Join(" ", keys.Select(Format)) + "]";
    }

    private static string Format(GeometryCellKey key)
    {
        return $"[{key.X},{key.Y},{key.Z}]";
    }

    private static string Format(Vector3 value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"({value.X:R},{value.Y:R},{value.Z:R})");
    }

    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
