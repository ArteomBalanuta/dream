using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public class WorldPhysicsBinder
{
    private readonly PhysicsIntegrator _physics;
    private readonly Dictionary<Cube, RigidBody> _cubeBodies = new();
    private readonly Dictionary<GeometryCellKey, List<RigidBody>> _geometryCellBodies = new();
    private readonly List<RigidBody> _mergedStaticCollisionBodies = new();
    private RigidBody? _floorBody;

    public WorldPhysicsBinder(PhysicsIntegrator physics)
    {
        _physics = physics;
    }

    public void AddFloorBody(int width, int depth)
    {
        AddCollisionBody(new GeometryCollisionBodyDescriptor(
            GeometryCollisionBodyKind.Floor,
            new Vector3(width / 2f, 0.5f, depth / 2f),
            new Vector3(width / 2f, 0.5f, depth / 2f),
            IsStatic: true));
    }

    public void AddCollisionBody(GeometryCollisionBodyDescriptor descriptor)
    {
        if (descriptor.Kind == GeometryCollisionBodyKind.Floor)
        {
            AddFloorBody(descriptor);
            return;
        }

        if (descriptor.CellKey == null)
        {
            throw new InvalidOperationException("Geometry collision descriptors require a geometry cell key.");
        }

        SyncCellCollisionBodies(descriptor.CellKey.Value, [descriptor]);
    }

    public void SyncCellCollisionBodies(
        GeometryCellKey cellKey,
        IReadOnlyList<GeometryCollisionBodyDescriptor> descriptors)
    {
        RemoveCellCollisionBodies(cellKey);

        if (descriptors.Count == 0)
        {
            return;
        }

        List<RigidBody> bodies = new(descriptors.Count);
        foreach (GeometryCollisionBodyDescriptor descriptor in descriptors)
        {
            if (descriptor.Kind == GeometryCollisionBodyKind.Floor)
            {
                AddFloorBody(descriptor);
                continue;
            }

            bodies.Add(AddGeometryBody(descriptor));
        }

        if (bodies.Count > 0)
        {
            _geometryCellBodies[cellKey] = bodies;
        }
    }

    public void SyncMergedStaticCollisionBodies(IReadOnlyList<GeometryCollisionBodyDescriptor> descriptors)
    {
        RemoveBodies(_mergedStaticCollisionBodies);
        _mergedStaticCollisionBodies.Clear();

        foreach (GeometryCollisionBodyDescriptor descriptor in descriptors)
        {
            if (descriptor.Kind == GeometryCollisionBodyKind.Floor)
            {
                AddFloorBody(descriptor);
                continue;
            }

            _mergedStaticCollisionBodies.Add(AddGeometryBody(descriptor));
        }
    }

    public void RemoveCellCollisionBodies(GeometryCellKey cellKey)
    {
        if (!_geometryCellBodies.TryGetValue(cellKey, out List<RigidBody>? bodies))
        {
            return;
        }

        RemoveBodies(bodies);

        _geometryCellBodies.Remove(cellKey);
    }

    private void RemoveBodies(IEnumerable<RigidBody> bodies)
    {
        foreach (RigidBody body in bodies)
        {
            _physics.World.RemoveRigidBody(body);
            _physics.MapCubeBodies.Remove(body);
            _physics.DynamicMapCubeBodies.Remove(body);
            body.MotionState?.Dispose();
            body.CollisionShape?.Dispose();
            body.Dispose();
        }
    }

    private void AddFloorBody(GeometryCollisionBodyDescriptor descriptor)
    {
        var shape = new BoxShape(descriptor.HalfExtents.X, descriptor.HalfExtents.Y, descriptor.HalfExtents.Z)
        {
            Margin = 0f
        };

        var floorCenter = descriptor.Center.ToBullet();
        var startTransform = Matrix.Translation(floorCenter);
        var motionState = new DefaultMotionState(startTransform);
        var rbInfo = new RigidBodyConstructionInfo(0f, motionState, shape, BulletSharp.Math.Vector3.Zero);

        var body = new RigidBody(rbInfo)
        {
            Friction = 0.8f,
            Restitution = 0.2f,
            AngularFactor = BulletSharp.Math.Vector3.Zero
        };

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);
        _floorBody = body;
    }

    public void AddCubeBody(Cube cube, bool isStatic)
    {
        var shape = new BoxShape(0.5f)
        {
            Margin = 0f
        };

        float mass = isStatic ? 0f : 1f;
        Vector3 position = cube.Position;
        var startTransform = Matrix.Translation(position.ToBullet());
        var motionState = new DefaultMotionState(startTransform);
        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            UserObject = (cube, position),
            Friction = 0.8f,
            Restitution = 0.2f,
            AngularFactor = BulletSharp.Math.Vector3.Zero
        };

        body.SetMassProps(mass, inertia);

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);
        if (!isStatic)
        {
            ConfigureDynamicBody(body, new Vector3(0.5f));
            _physics.DynamicMapCubeBodies.Add(body);
        }

        _cubeBodies[cube] = body;
    }

    private RigidBody AddGeometryBody(GeometryCollisionBodyDescriptor descriptor)
    {
        var shape = new BoxShape(descriptor.HalfExtents.X, descriptor.HalfExtents.Y, descriptor.HalfExtents.Z)
        {
            Margin = 0f
        };

        float mass = descriptor.IsStatic ? 0f : 1f;
        var startTransform = Matrix.Translation(descriptor.Center.ToBullet());
        var motionState = new DefaultMotionState(startTransform);
        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        object userObject = descriptor.CompatibilityCube != null
            ? (descriptor.CompatibilityCube, descriptor.Center)
            : descriptor.FracturePieceIndex.HasValue && descriptor.CellKey.HasValue
                ? new GeometryDynamicBodyReference(
                    descriptor.CellKey.Value,
                    descriptor.FracturePieceIndex.Value,
                    descriptor.Center)
                : (descriptor.CellKey, descriptor.SubcellKey, descriptor.Center);

        var body = new RigidBody(rbInfo)
        {
            UserObject = userObject,
            Friction = 0.8f,
            Restitution = 0.2f,
            AngularFactor = BulletSharp.Math.Vector3.Zero
        };

        body.SetMassProps(mass, inertia);
        if (!descriptor.IsStatic)
        {
            ConfigureDynamicBody(body, descriptor.HalfExtents);
        }

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);
        if (!descriptor.IsStatic)
        {
            _physics.DynamicMapCubeBodies.Add(body);
        }

        return body;
    }

    public bool RemoveCubeBody(Cube cube)
    {
        if (!_cubeBodies.TryGetValue(cube, out RigidBody? body))
        {
            return false;
        }

        _physics.World.RemoveRigidBody(body);
        _physics.MapCubeBodies.Remove(body);
        _physics.DynamicMapCubeBodies.Remove(body);
        _cubeBodies.Remove(cube);
        body.MotionState?.Dispose();
        body.CollisionShape?.Dispose();
        body.Dispose();
        return true;
    }

    public int RemoveCompatibilityCubeBodies(Cube cube)
    {
        ArgumentNullException.ThrowIfNull(cube);

        RigidBody[] bodies = _physics.MapCubeBodies
            .Where(body => body.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube))
            .ToArray();
        if (bodies.Length == 0)
        {
            return 0;
        }

        HashSet<RigidBody> bodySet = bodies.ToHashSet();
        RemoveBodies(bodies);
        List<GeometryCellKey> emptyCells = [];
        foreach ((GeometryCellKey cellKey, List<RigidBody> cellBodies) in _geometryCellBodies)
        {
            cellBodies.RemoveAll(bodySet.Contains);
            if (cellBodies.Count == 0)
            {
                emptyCells.Add(cellKey);
            }
        }

        foreach (GeometryCellKey cellKey in emptyCells)
        {
            _geometryCellBodies.Remove(cellKey);
        }

        _mergedStaticCollisionBodies.RemoveAll(bodySet.Contains);
        _cubeBodies.Remove(cube);
        return bodies.Length;
    }

    private static void ConfigureDynamicBody(RigidBody body, Vector3 halfExtents)
    {
        body.ActivationState = ActivationState.DisableDeactivation;
        body.ContactProcessingThreshold = 0f;
        float smallestHalfExtent = MathF.Max(0.01f, MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z)));
        body.CcdMotionThreshold = smallestHalfExtent * 0.5f;
        body.CcdSweptSphereRadius = smallestHalfExtent * 0.8f;
        body.Activate(true);
    }
}
