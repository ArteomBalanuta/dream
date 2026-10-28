using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.World;

// SCALE-7(c): the server's map is headless. It skips render block descriptors, merged sculpture
// render blocks and render-dirty tracking, and everything the simulation and the wire depend on
// must stay identical to a rendering map's: geometry records, collision descriptors, physics
// bodies (same bodies in the same order), replicated cubes and the world checksum.
public class MapHeadlessTests
{
    [Theory]
    // 56 (the startup floor size) has the subdivided sculpture cells, whose carving rebuilds the
    // merged static collision bodies; it is much slower per step, so it runs fewer.
    [InlineData(21, 25, 400)]
    [InlineData(22, 25, 400)]
    [InlineData(23, 56, 60)]
    public void HeadlessAndRenderingMaps_SameEdits_HaveIdenticalSimulationState(int seed, int floorSize, int steps)
    {
        using PhysicsIntegrator renderingPhysics = new();
        using PhysicsIntegrator headlessPhysics = new();
        Map rendering = new(renderingPhysics, generateStartupTerrain: true, width: floorSize, depth: floorSize, replicatedCubeBodies: true);
        Map headless = new(headlessPhysics, generateStartupTerrain: true, width: floorSize, depth: floorSize, replicatedCubeBodies: true, headless: true);
        Assert.False(rendering.IsHeadless);
        Assert.True(headless.IsHeadless);
        AssertSameSimulationState(rendering, headless, renderingPhysics, headlessPhysics);

        Random random = new(seed);
        for (int step = 0; step < steps; step++)
        {
            MapEditScript.Step(random, [rendering, headless]);
            if (step % 20 == 19)
            {
                AssertSameSimulationState(rendering, headless, renderingPhysics, headlessPhysics);
            }
        }

        // The bodies then simulate identically.
        for (int tick = 0; tick < 30; tick++)
        {
            renderingPhysics.StepSimulationFixed(1f / 64f);
            headlessPhysics.StepSimulationFixed(1f / 64f);
        }

        AssertSameSimulationState(rendering, headless, renderingPhysics, headlessPhysics);
        Assert.Empty(rendering.FindInvariantViolations());
        Assert.Empty(headless.FindInvariantViolations());
    }

    [Fact]
    public void HeadlessMap_RenderExportsThrow_AndStaticContentVersionNeverMoves()
    {
        Map map = new(generateStartupTerrain: true, width: 56, depth: 56, headless: true);
        long version = map.StaticContentVersion;

        Assert.True(map.AddCube(new Vector3(4, 3, 4), isStatic: true, withPhysics: false));
        Assert.True(map.AddCube(new Vector3(6, 3, 6), withPhysics: false));
        Assert.True(map.FractureCubeIntoPieces(new GeometryCellKey(6, 3, 6)));
        GeometryCellKey sculptureCell = map.GeometryCells.Keys.First(key =>
            key.Y > 0 && map.GeometryCells[key].SubdivisionState != null);
        map.ApplyGeometrySubcellSolid(sculptureCell, new GeometrySubcellKey(0, 0, 0), isSolid: false);

        Assert.Equal(version, map.StaticContentVersion);
        Assert.Throws<InvalidOperationException>(() => map.CaptureFullGeometryRenderExport());
        Assert.Throws<InvalidOperationException>(() => map.CaptureGeometryRenderExport(Vector3.Zero, 4f));
        Assert.Throws<InvalidOperationException>(() => map.CapturePresentationGeometryRenderExport(Vector3.Zero, 4f));
        // Collision export still works and carries the fractured cell's pieces.
        GeometryCollisionExport collision = map.CaptureGeometryCollisionExport();
        Assert.Contains(collision.Bodies, body => body.CellKey == new GeometryCellKey(6, 3, 6) && body.FracturePieceIndex.HasValue);
    }

    [Fact]
    public void ServerRuntimeMap_IsHeadlessByDefault_AndRenderingOnRequest()
    {
        using WorldSessionRuntime headless = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using WorldSessionRuntime rendering = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), headlessMap: false);

        Assert.True(headless.Map.IsHeadless);
        Assert.False(rendering.Map.IsHeadless);
        Assert.Equal(WorldStateChecksum.Compute(rendering.Map), WorldStateChecksum.Compute(headless.Map));

        // A rebuilt map keeps the runtime's choice.
        headless.RebuildMapForFloorSize(12);
        Assert.True(headless.Map.IsHeadless);
    }

    // Edits on a headless map allocate no render descriptors: a cube move across cells costs the
    // moved record only (as on a rendering map), and a rubble move costs the piece copy only.
    [Fact]
    public void HeadlessMap_FractureAndMove_AllocateNoMoreThanRenderingMap()
    {
        long Measure(bool headless)
        {
            Map map = new(width: 24, depth: 24, headless: headless);
            int next = 0;
            Vector3[] cells = Enumerable.Range(0, 200).Select(i => new Vector3(1 + (i % 20), 1 + (i / 20 % 5), 1 + (i / 100))).ToArray();
            return AllocationAssert.MeasureBytesPerIteration(() =>
            {
                Vector3 cell = cells[next++ % cells.Length];
                GeometryCellKey key = new((int)cell.X, (int)cell.Y, (int)cell.Z);
                map.RemoveRubble(key);
                map.AddCube(cell, withPhysics: false);
                map.FractureCubeIntoPieces(key);
            });
        }

        long rendering = Measure(headless: false);
        long headlessBytes = Measure(headless: true);
        Assert.True(headlessBytes < rendering, $"headless {headlessBytes} B vs rendering {rendering} B per fracture");
    }

    private static void AssertSameSimulationState(
        Map expected,
        Map actual,
        PhysicsIntegrator expectedPhysics,
        PhysicsIntegrator actualPhysics)
    {
        Assert.Equal(WorldStateChecksum.Compute(expected), WorldStateChecksum.Compute(actual));
        Assert.Equal(WorldStateChecksum.ComputeFull(expected), WorldStateChecksum.Compute(actual));
        Assert.Equal(expected.GeometryCells.Keys, actual.GeometryCells.Keys);
        Assert.Equal(expected.RubbleCells.Keys, actual.RubbleCells.Keys);
        Assert.Equal(
            expected.SolidCubes.Select(cube => cube.Position),
            actual.SolidCubes.Select(cube => cube.Position));
        Assert.Equal(
            expected.CaptureGeometryCollisionExport().Bodies.Select(Describe),
            actual.CaptureGeometryCollisionExport().Bodies.Select(Describe));
        Assert.Equal(DescribeBodies(expectedPhysics), DescribeBodies(actualPhysics));
    }

    private static string Describe(GeometryCollisionBodyDescriptor body)
    {
        return $"{body.Kind} {body.Center} {body.HalfExtents} {body.IsStatic} {body.CellKey} {body.SubcellKey} {body.FracturePieceIndex} {body.CompatibilityCube?.Position}";
    }

    private static List<string> DescribeBodies(PhysicsIntegrator physics)
    {
        List<string> bodies = [];
        AlignedCollisionObjectArray objects = physics.World.CollisionObjectArray;
        for (int i = 0; i < objects.Count; i++)
        {
            CollisionObject body = objects[i];
            bodies.Add($"{body.CollisionShape.ShapeType} {body.WorldTransform.Origin} {body.CollisionFlags} {body.IsActive}");
        }

        return bodies;
    }
}
