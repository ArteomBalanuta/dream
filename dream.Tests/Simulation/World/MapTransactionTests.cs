using BulletSharp;
using BulletSharp.Math;
using Xunit;
using Dream.Rendering.World;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.World;

// Regression coverage for the map consistency work: multi-pass physics commit for chain pushes,
// released fractured cells, hit rays against real cube bounds, chunk-queue merging and
// cross-index invariants.
public class MapTransactionTests
{
    [Fact]
    public void ChainPush_LeadingAndTrailingCubesMoveInTheSameTick()
    {
        // Bodies are listed trailing-first: a single ordered pass rejected the trailing cube
        // because the leading cube had not left its cell yet.
        Map map = new(physics: null, loadVisualAssets: false, width: 12, depth: 12);
        Cube trailing = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(3f, 1f, 3f), withPhysics: false));
        Cube leading = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(4f, 1f, 3f), withPhysics: false));
        RigidBody trailingBody = CreateBody(new TkVector3(4.05f, 1.5f, 3.5f), trailing);
        RigidBody leadingBody = CreateBody(new TkVector3(5.05f, 1.5f, 3.5f), leading);

        PhysicsSyncResult result = new PhysicsSyncSystem().SyncDynamicMapBodies(map, [trailingBody, leadingBody]);

        Assert.Equal(2, result.MovedCubes.Count);
        Assert.Same(trailing, map.TryGetCubeAtCell(4, 1, 3));
        Assert.Same(leading, map.TryGetCubeAtCell(5, 1, 3));
        Assert.Empty(map.FindInvariantViolations());
    }

    [Fact]
    public void FracturedCell_IsReleasedForPlacementWhileRubbleRemains()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 10, depth: 10);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: false);
        GeometryCellKey cell = new(3, 1, 3);

        Assert.True(map.FractureCubeIntoPieces(cell));
        Cube placed = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(3f, 1f, 3f), withPhysics: false));

        Assert.Same(placed, map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(map.TryGetRubbleAtCell(cell));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new TkVector3(3.5f, 1.5f, 3.5f), 6f);
        Assert.Contains(placed, export.DynamicCompatibilityCubes);
        Assert.Contains(export.DynamicBlocks, block => block.CellKey == cell);
        Assert.Empty(map.FindInvariantViolations());
    }

    [Fact]
    public void RubbleAndCubeInTheSameOriginCell_KeepSeparatePhysicsBodies()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 10, depth: 10);
        map.AddCube(new TkVector3(3f, 1f, 3f));
        map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));
        int rubbleBodies = physics.DynamicMapCubeBodies.Count(body => body.UserObject is GeometryDynamicBodyReference);
        Assert.True(rubbleBodies > 0);

        Cube placed = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(3f, 1f, 3f)));

        Assert.Equal(rubbleBodies, physics.DynamicMapCubeBodies.Count(body => body.UserObject is GeometryDynamicBodyReference));
        Assert.Contains(
            physics.MapCubeBodies,
            body => body.UserObject is ValueTuple<Cube, TkVector3> cubeBody && ReferenceEquals(cubeBody.Item1, placed));
    }

    [Fact]
    public void HitRay_UsesTheCubesActualBoxNotItsCell()
    {
        // A cube pushed to x=3.95 still owns cell 3 but visibly spans 3.45..4.45.
        Map map = new(physics: null, loadVisualAssets: false, width: 10, depth: 10);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(3f, 1f, 3f), withPhysics: false));
        map.UpdateCubePosition(cube, new TkVector3(3.95f, 1.5f, 3.5f), syncPhysics: false);
        Assert.Same(cube, map.TryGetCubeAtCell(3, 1, 3));

        // Aim at the visible part that lies in cell 4.
        Cube? visibleHit = map.TryGetFirstSolidCubeAlongRay(new TkVector3(4.3f, 1.5f, 6f), -TkVector3.UnitZ, 4f);
        // Aim at the empty part of cell 3.
        Cube? emptyHit = map.TryGetFirstSolidCubeAlongRay(new TkVector3(3.2f, 1.5f, 6f), -TkVector3.UnitZ, 4f);

        Assert.Same(cube, visibleHit);
        Assert.Null(emptyHit);
    }

    [Fact]
    public void InvariantCheck_ReportsAMissingCubeIndex()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 10, depth: 10);
        Assert.NotNull(map.TryAddCube(new TkVector3(3f, 1f, 3f), withPhysics: false));
        Assert.Empty(map.FindInvariantViolations());

        map.Cubes[3, 1, 3] = null;

        Assert.NotEmpty(map.FindInvariantViolations());
    }

    [Fact]
    public void SupersededChunkRequest_KeepsItsDirtyChunks()
    {
        StaticWorldChunkKey emptied = new(0, 0, 0);
        StaticWorldChunkKey changed = new(1, 0, 0);
        StaticWorldChunkBuildRequest older = new(1, new HashSet<StaticWorldChunkKey> { emptied }, []);
        StaticWorldChunkBuildRequest newer = new(2, new HashSet<StaticWorldChunkKey> { changed }, []);

        StaticWorldChunkBuildRequest merged = StaticWorldChunkBuildRequest.MergeSuperseded(older, newer);

        Assert.Equal(2, merged.ExportSignature);
        Assert.Contains(emptied, merged.DirtyChunks);
        Assert.Contains(changed, merged.DirtyChunks);
    }

    private static RigidBody CreateBody(TkVector3 position, Cube cube)
    {
        BoxShape shape = new(0.5f);
        DefaultMotionState motionState = new(Matrix.Translation(position.X, position.Y, position.Z));
        return new RigidBody(new RigidBodyConstructionInfo(1f, motionState, shape))
        {
            UserObject = (cube, cube.Position)
        };
    }
}
