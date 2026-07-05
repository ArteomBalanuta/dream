using BulletSharp;
using BulletSharp.Math;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

public class PhysicsSyncSystemTests
{
    [Fact]
    public void SyncDynamicMapBodies_SnapshotsBodiesBeforeIterating()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);

        List<RigidBody> bodies = [];
        bool allowMutation = false;
        RigidBody firstBody = CreateBody(
            new BulletSharp.Math.Vector3(4.1f, 1.5f, 3.5f),
            onGetWorldTransform: () =>
            {
                if (allowMutation && bodies.Count > 1)
                {
                    bodies.RemoveAt(1);
                }
            });
        firstBody.UserObject = (cube, cube.Position);

        RigidBody secondBody = CreateBody(new BulletSharp.Math.Vector3(8.5f, 1.5f, 8.5f));
        bodies.Add(firstBody);
        bodies.Add(secondBody);
        allowMutation = true;
        PhysicsSyncSystem sync = new();

        sync.SyncDynamicMapBodies(map, bodies);

        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.Same(cube, map.TryGetCubeAtCell(4, 1, 3));
        (Cube SyncedCube, TkVector3 SyncedPosition) syncedUserObject =
            Assert.IsType<(Cube SyncedCube, TkVector3 SyncedPosition)>(firstBody.UserObject);
        Assert.Same(cube, syncedUserObject.SyncedCube);
        Assert.Equal(new TkVector3(4.1f, 1.5f, 3.5f), syncedUserObject.SyncedPosition);
    }

    [Fact]
    public void SyncDynamicMapBodies_UpdatesFracturePieceRenderCenterFromDynamicBody()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: false);
        GeometryCellKey cell = new(3, 1, 3);
        map.FractureCubeIntoPieces(cell);
        GeometryRenderBlockDescriptor firstPieceBefore = map
            .CaptureGeometryRenderExport(new TkVector3(3.5f, 1.5f, 3.5f), 4f)
            .DynamicBlocks
            .First(block => block.CellKey == cell);
        TkVector3 movedCenter = firstPieceBefore.Center + new TkVector3(0.42f, 0f, 0.18f);
        RigidBody body = CreateBody(movedCenter.ToBullet());
        body.UserObject = new GeometryDynamicBodyReference(cell, PieceIndex: 0, firstPieceBefore.Center);
        PhysicsSyncSystem sync = new();

        sync.SyncDynamicMapBodies(map, [body]);

        GeometryRenderExport afterExport = map.CaptureGeometryRenderExport(new TkVector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryRenderBlockDescriptor firstPieceAfter = afterExport
            .DynamicBlocks
            .First(block => block.CellKey == cell);
        Assert.Equal(movedCenter, firstPieceAfter.Center);
        Assert.Empty(afterExport.DirtyCells);
    }

    [Fact]
    public void SyncDynamicMapBodies_DoesNotReplaceActivePhysicsBodyWhenCubeCrossesCellBoundary()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: true);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);
        RigidBody body = Assert.Single(physics.DynamicMapCubeBodies);

        map.UpdateCubePosition(cube, new TkVector3(4.2f, 1.5f, 3.5f), syncPhysics: false);

        Assert.Contains(body, physics.DynamicMapCubeBodies);
        Assert.Same(cube, map.TryGetCubeAtCell(4, 1, 3));
        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
    }

    [Fact]
    public void RemoveCube_RemovesCompatibilityBodyEvenAfterPhysicsDrivenCellDrift()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: true);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);
        RigidBody body = Assert.Single(physics.DynamicMapCubeBodies);

        map.UpdateCubePosition(cube, new TkVector3(4.2f, 1.5f, 3.5f), syncPhysics: false);

        Assert.True(map.RemoveCube(cube));

        Assert.DoesNotContain(body, physics.MapCubeBodies);
        Assert.DoesNotContain(body, physics.DynamicMapCubeBodies);
    }

    [Fact]
    public void SyncDynamicMapBodies_WhenTwoDynamicCubesResolveToSameCell_KeepsDistinctLogicalOwnership()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 10, depth: 10);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: false);
        map.AddCube(new TkVector3(5f, 1f, 3f), withPhysics: false);
        Cube firstCube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);
        Cube secondCube = Assert.IsType<Cube>(map.Cubes[5, 1, 3]);
        RigidBody firstBody = CreateBody(new BulletSharp.Math.Vector3(4.2f, 1.5f, 3.5f));
        RigidBody secondBody = CreateBody(new BulletSharp.Math.Vector3(4.8f, 1.5f, 3.5f));
        firstBody.UserObject = (firstCube, firstCube.Position);
        secondBody.UserObject = (secondCube, secondCube.Position);
        PhysicsSyncSystem sync = new();

        sync.SyncDynamicMapBodies(map, [firstBody, secondBody]);

        Assert.Same(firstCube, map.TryGetCubeAtCell(4, 1, 3));
        Assert.Same(secondCube, map.TryGetCubeAtCell(5, 1, 3));
        Assert.NotEqual(firstCube.Position, secondCube.Position);
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new TkVector3(4.5f, 1.5f, 3.5f), 4f);
        Assert.Contains(firstCube, export.DynamicCompatibilityCubes);
        Assert.Contains(secondCube, export.DynamicCompatibilityCubes);
    }

    [Fact]
    public void SyncDynamicMapBodies_WhenDynamicCubeResolvesIntoFracturedCell_PreservesFractureOwnership()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 10, depth: 10);
        map.AddCube(new TkVector3(4f, 1f, 4f), withPhysics: false);
        map.FractureCubeIntoPieces(new GeometryCellKey(4, 1, 4));
        map.AddCube(new TkVector3(6f, 1f, 4f), withPhysics: false);
        Cube movingCube = Assert.IsType<Cube>(map.Cubes[6, 1, 4]);
        RigidBody body = CreateBody(new BulletSharp.Math.Vector3(4.3f, 1.5f, 4.4f));
        body.UserObject = (movingCube, movingCube.Position);
        PhysicsSyncSystem sync = new();

        sync.SyncDynamicMapBodies(map, [body]);

        GeometryCellRecord fracturedCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(4, 1, 4));
        Assert.NotNull(fracturedCell.FracturePieces);
        Assert.Same(movingCube, map.TryGetCubeAtCell(6, 1, 4));
        Assert.NotSame(movingCube, map.TryGetCubeAtCell(4, 1, 4));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new TkVector3(4.5f, 1.5f, 4.5f), 6f);
        Assert.Contains(movingCube, export.DynamicCompatibilityCubes);
        Assert.Contains(export.DynamicBlocks, block => block.CellKey == new GeometryCellKey(4, 1, 4));
    }

    private static RigidBody CreateBody(
        BulletSharp.Math.Vector3 position,
        Action? onGetWorldTransform = null)
    {
        MotionState motionState = new CallbackMotionState(position, onGetWorldTransform);
        BoxShape shape = new(0.5f, 0.5f, 0.5f);
        RigidBodyConstructionInfo rbInfo = new(1f, motionState, shape, BulletSharp.Math.Vector3.Zero);
        return new RigidBody(rbInfo)
        {
            UserObject = null
        };
    }

    private sealed class CallbackMotionState : MotionState
    {
        private Matrix _worldTransform;
        private readonly Action? _onGetWorldTransform;

        public CallbackMotionState(BulletSharp.Math.Vector3 position, Action? onGetWorldTransform)
        {
            _worldTransform = Matrix.Translation(position);
            _onGetWorldTransform = onGetWorldTransform;
        }

        public override void GetWorldTransform(out Matrix worldTrans)
        {
            _onGetWorldTransform?.Invoke();
            worldTrans = _worldTransform;
        }

        public override void SetWorldTransform(ref Matrix worldTrans)
        {
            _worldTransform = worldTrans;
        }
    }
}
