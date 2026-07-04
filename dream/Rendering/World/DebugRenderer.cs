using BulletSharp;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;


public class DebugRenderer
{
    public const int MaxCollisionBodiesPerFrame = 1024;
    private readonly Render _render;

    public DebugRenderer(Render render)
    {
        _render = render;
    }

    public void DrawCollisionBodies(IEnumerable<RigidBody> bodies, Camera camera)
    {
        foreach (RigidBody body in SelectCollisionBodiesForDebug(bodies, camera))
        {
            _render.DrawCollisionBody(body, camera);
        }
    }

    public static IReadOnlyList<RigidBody> SelectCollisionBodiesForDebug(
        IEnumerable<RigidBody> bodies,
        Camera camera,
        int maxBodies = MaxCollisionBodiesPerFrame)
    {
        if (maxBodies <= 0)
        {
            return Array.Empty<RigidBody>();
        }

        List<(RigidBody Body, float DistanceSquared)> visibleBodies = [];
        foreach (RigidBody body in bodies)
        {
            if (!ShouldDrawCollisionBody(body) ||
                !TryResolveDebugPosition(body, out Vector3 position) ||
                !WorldVisibility.IsPositionVisible(camera.Position, camera.Front, position))
            {
                continue;
            }

            visibleBodies.Add((body, (position - camera.Position).LengthSquared));
        }

        return visibleBodies
            .OrderBy(entry => entry.DistanceSquared)
            .Take(maxBodies)
            .Select(entry => entry.Body)
            .ToArray();
    }

    public static bool ShouldDrawCollisionBody(RigidBody body)
    {
        return body.UserObject is (Cube, Vector3) ||
            body.UserObject is ValueTuple<GeometryCellKey?, GeometrySubcellKey?, Vector3> ||
            body.UserObject is GeometryDynamicBodyReference;
    }

    private static bool TryResolveDebugPosition(RigidBody body, out Vector3 position)
    {
        switch (body.UserObject)
        {
            case ValueTuple<Cube, Vector3> cubeBody:
                position = cubeBody.Item2;
                return true;
            case ValueTuple<GeometryCellKey?, GeometrySubcellKey?, Vector3> geometryBody:
                position = geometryBody.Item3;
                return true;
            case GeometryDynamicBodyReference:
                body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
                position = transform.Origin.ToOpenTK();
                return true;
            default:
                position = default;
                return false;
        }
    }
}
