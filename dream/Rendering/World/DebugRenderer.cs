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
    private readonly CollisionDebugBodySelector _collisionBodySelector = new();

    public DebugRenderer(Render render)
    {
        _render = render;
    }

    public void DrawCollisionBodies(IEnumerable<RigidBody> bodies, Camera camera)
    {
        IReadOnlyList<RigidBody> selected = _collisionBodySelector.Select(bodies, camera);
        for (int index = 0; index < selected.Count; index++)
        {
            _render.DrawCollisionBody(selected[index], camera);
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

        return [.. new CollisionDebugBodySelector().Select(bodies, camera, maxBodies)];
    }

    public static bool ShouldDrawCollisionBody(RigidBody body)
    {
        return body.UserObject is (Cube, Vector3) ||
            body.UserObject is ValueTuple<GeometryCellKey?, GeometrySubcellKey?, Vector3> ||
            body.UserObject is GeometryDynamicBodyReference;
    }

    internal static bool TryResolveDebugPosition(RigidBody body, out Vector3 position)
    {
        if (body.MotionState != null)
        {
            body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
            position = transform.Origin.ToOpenTK();
            return true;
        }

        switch (body.UserObject)
        {
            case ValueTuple<Cube, Vector3> cubeBody:
                position = cubeBody.Item2;
                return true;
            case ValueTuple<GeometryCellKey?, GeometrySubcellKey?, Vector3> geometryBody:
                position = geometryBody.Item3;
                return true;
            default:
                position = default;
                return false;
        }
    }
}

// Picks the nearest visible collision bodies for debug drawing into buffers it reuses, so a
// frame allocates nothing once the buffers have grown. The result is the same as a stable
// OrderBy on distance followed by Take: ties keep their input order. The returned list is
// reused by the next Select call.
public sealed class CollisionDebugBodySelector
{
    private static readonly Comparison<Candidate> ByDistanceThenInputOrder = static (left, right) =>
    {
        int byDistance = left.DistanceSquared.CompareTo(right.DistanceSquared);
        return byDistance != 0 ? byDistance : left.InputOrder.CompareTo(right.InputOrder);
    };

    private readonly List<Candidate> _candidates = [];
    private readonly List<RigidBody> _selected = [];

    public IReadOnlyList<RigidBody> Select(
        IEnumerable<RigidBody> bodies,
        Camera camera,
        int maxBodies = DebugRenderer.MaxCollisionBodiesPerFrame)
    {
        _candidates.Clear();
        _selected.Clear();
        if (maxBodies <= 0)
        {
            return _selected;
        }

        if (bodies is IReadOnlyList<RigidBody> bodyList)
        {
            for (int index = 0; index < bodyList.Count; index++)
            {
                AddCandidate(bodyList[index], camera);
            }
        }
        else
        {
            foreach (RigidBody body in bodies)
            {
                AddCandidate(body, camera);
            }
        }

        _candidates.Sort(ByDistanceThenInputOrder);
        int count = Math.Min(maxBodies, _candidates.Count);
        for (int index = 0; index < count; index++)
        {
            _selected.Add(_candidates[index].Body);
        }

        _candidates.Clear();
        return _selected;
    }

    private void AddCandidate(RigidBody body, Camera camera)
    {
        if (!DebugRenderer.ShouldDrawCollisionBody(body) ||
            !DebugRenderer.TryResolveDebugPosition(body, out Vector3 position) ||
            !WorldVisibility.IsPositionVisible(camera.Position, camera.Front, position))
        {
            return;
        }

        _candidates.Add(new Candidate(body, (position - camera.Position).LengthSquared, _candidates.Count));
    }

    private readonly record struct Candidate(RigidBody Body, float DistanceSquared, int InputOrder);
}
