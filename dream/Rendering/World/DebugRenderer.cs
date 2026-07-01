using BulletSharp;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.World;

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
            if (body.UserObject is (Cube, Vector3))
            {
                _render.DrawCollisionBody(body, camera);
            }
        }
    }
}
