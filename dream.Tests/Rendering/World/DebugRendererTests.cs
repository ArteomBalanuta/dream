using System.Runtime.CompilerServices;
using BulletSharp;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;

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
}
