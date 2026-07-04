using System.Runtime.CompilerServices;
using BulletSharp;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using BMatrix = BulletSharp.Math.Matrix;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Rendering.World;

public class DebugRendererTests
{
    [Fact]
    public void ShouldDrawCollisionBody_Returns_True_For_Geometry_Subcell_Bodies()
    {
        RigidBody body = (RigidBody)RuntimeHelpers.GetUninitializedObject(typeof(RigidBody));
        GC.SuppressFinalize(body);
        body.UserObject = ((GeometryCellKey?)new GeometryCellKey(3, 1, 3), (GeometrySubcellKey?)new GeometrySubcellKey(0, 0, 0), new Vector3(3.5f, 1.5f, 3.5f));

        bool shouldDraw = DebugRenderer.ShouldDrawCollisionBody(body);

        Assert.True(shouldDraw);
    }

    [Fact]
    public void SelectCollisionBodiesForDebug_Culls_Distant_Bodies()
    {
        Camera camera = new(Vector3.Zero);
        RigidBody visibleBody = CreateGeometryBody(new Vector3(0f, 0f, -4f));
        RigidBody distantBody = CreateGeometryBody(new Vector3(0f, 0f, -100f));

        IReadOnlyList<RigidBody> selected = DebugRenderer.SelectCollisionBodiesForDebug(
            [visibleBody, distantBody],
            camera);

        RigidBody selectedBody = Assert.Single(selected);
        Assert.Same(visibleBody, selectedBody);
    }

    [Fact]
    public void SelectCollisionBodiesForDebug_Applies_Nearest_Body_Budget()
    {
        Camera camera = new(Vector3.Zero);
        RigidBody nearest = CreateGeometryBody(new Vector3(0f, 0f, -3f));
        RigidBody middle = CreateGeometryBody(new Vector3(0f, 0f, -5f));
        RigidBody farthest = CreateGeometryBody(new Vector3(0f, 0f, -7f));

        IReadOnlyList<RigidBody> selected = DebugRenderer.SelectCollisionBodiesForDebug(
            [farthest, nearest, middle],
            camera,
            maxBodies: 2);

        Assert.Equal([nearest, middle], selected);
    }

    [Fact]
    public void SelectCollisionBodiesForDebug_UsesLiveTransformForMovedCubeBodies()
    {
        Camera camera = new(TkVector3.Zero);
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new TkVector3(100f, 1f, 100f)
        };
        RigidBody body = CreateCubeBody(cube, livePosition: new TkVector3(0f, 0f, -4f));
        body.UserObject = (cube, new TkVector3(100f, 1f, 100f));

        IReadOnlyList<RigidBody> selected = DebugRenderer.SelectCollisionBodiesForDebug(
            [body],
            camera);

        Assert.Single(selected);
    }

    private static RigidBody CreateGeometryBody(Vector3 position)
    {
        RigidBody body = (RigidBody)RuntimeHelpers.GetUninitializedObject(typeof(RigidBody));
        GC.SuppressFinalize(body);
        body.UserObject = ((GeometryCellKey?)new GeometryCellKey(3, 1, 3), (GeometrySubcellKey?)null, position);
        return body;
    }

    private static RigidBody CreateCubeBody(Cube cube, TkVector3 livePosition)
    {
        BoxShape shape = new(0.5f);
        BMatrix transform = BMatrix.Translation(livePosition.ToBullet());
        DefaultMotionState motionState = new(transform);
        RigidBodyConstructionInfo rbInfo = new(1f, motionState, shape, BulletSharp.Math.Vector3.Zero);
        return new RigidBody(rbInfo)
        {
            UserObject = (cube, livePosition)
        };
    }
}
