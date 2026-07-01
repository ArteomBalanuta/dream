using BulletSharp;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;


public class DebugRenderer
{
    private readonly Render _render;

    public DebugRenderer(Render render)
    {
        _render = render;
    }

    public void DrawCollisionBodies(IEnumerable<RigidBody> bodies, Camera camera)
    {
        foreach (RigidBody body in bodies)
        {
            if (ShouldDrawCollisionBody(body))
            {
                _render.DrawCollisionBody(body, camera);
            }
        }
    }

    public static bool ShouldDrawCollisionBody(RigidBody body)
    {
        return body.UserObject is (Cube, Vector3) ||
            body.UserObject is ValueTuple<GeometryCellKey?, GeometrySubcellKey?, Vector3>;
    }
}
