using System.Runtime.CompilerServices;
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
    private readonly bool _replicatedCubeBodies;
    private readonly Dictionary<Cube, RigidBody> _cubeBodies = new();
    private readonly Dictionary<GeometryCellKey, List<RigidBody>> _geometryCellBodies = new();
    private readonly List<RigidBody> _mergedStaticCollisionBodies = new();
    private readonly Dictionary<GeometryCellKey, List<RigidBody>> _rubbleBodies = new();
    // Empty cell body lists left behind when a moving cube leaves its last cell; reused when a
    // moving cube enters a cell with no list, so per-move reindexing allocates nothing.
    private readonly Stack<List<RigidBody>> _spareCellBodyLists = new();
    private RigidBody? _floorBody;

    // Replicated cube bodies belong to a client mirror map: full cubes are moved only by
    // authoritative server deltas, so they are kinematic and never enter the local sync list.
    public WorldPhysicsBinder(PhysicsIntegrator physics, bool replicatedCubeBodies = false)
    {
        _physics = physics;
        _replicatedCubeBodies = replicatedCubeBodies;
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

    // Rubble bodies are indexed apart from cell bodies so a cube in the same origin cell and its
    // old rubble never replace each other's bodies.
    public void SyncRubbleBodies(GeometryCellKey originCell, IReadOnlyList<GeometryCollisionBodyDescriptor> descriptors)
    {
        RemoveRubbleBodies(originCell);
        if (descriptors.Count == 0)
        {
            return;
        }

        _rubbleBodies[originCell] = descriptors.Select(AddGeometryBody).ToList();
    }

    public void RemoveRubbleBodies(GeometryCellKey originCell)
    {
        if (_rubbleBodies.Remove(originCell, out List<RigidBody>? bodies))
        {
            RemoveBodies(bodies);
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

    public void ReindexCompatibilityCubeBody(Cube cube, GeometryCellKey oldCellKey, GeometryCellKey newCellKey)
    {
        ArgumentNullException.ThrowIfNull(cube);

        if (oldCellKey == newCellKey ||
            !_geometryCellBodies.TryGetValue(oldCellKey, out List<RigidBody>? oldBodies) ||
            IndexOfCompatibilityCubeBody(oldBodies, cube) < 0)
        {
            return;
        }

        if (!_geometryCellBodies.TryGetValue(newCellKey, out List<RigidBody>? newBodies))
        {
            newBodies = RentCellBodyList();
            _geometryCellBodies[newCellKey] = newBodies;
        }

        // Compacts the old list in place and appends the cube's bodies to the new one, keeping
        // both lists' relative order exactly as the former Where/RemoveAll/AddRange did.
        int kept = 0;
        for (int i = 0; i < oldBodies.Count; i++)
        {
            RigidBody body = oldBodies[i];
            if (IsCompatibilityCubeBody(body, cube))
            {
                newBodies.Add(body);
            }
            else
            {
                oldBodies[kept++] = body;
            }
        }

        oldBodies.RemoveRange(kept, oldBodies.Count - kept);
        if (oldBodies.Count == 0)
        {
            // An emptied cell leaves the index so "has bodies" checks still see no entry; its
            // list is recycled for the next cell a moving cube enters.
            _geometryCellBodies.Remove(oldCellKey);
            _spareCellBodyLists.Push(oldBodies);
        }
    }

    // Moves a compatibility cube's existing body to an authoritative center instead of
    // disposing and recreating it. Returns false when the cube has no body indexed under
    // oldCellKey, so the caller can fall back to a full cell rebuild.
    public bool TryMoveCompatibilityCubeBody(
        Cube cube,
        GeometryCellKey oldCellKey,
        GeometryCellKey newCellKey,
        Vector3 center)
    {
        ArgumentNullException.ThrowIfNull(cube);

        if (!_geometryCellBodies.TryGetValue(oldCellKey, out List<RigidBody>? oldBodies))
        {
            return false;
        }

        int index = IndexOfCompatibilityCubeBody(oldBodies, cube);
        if (index < 0)
        {
            return false;
        }

        RigidBody body = oldBodies[index];

        Matrix transform = Matrix.Translation(center.ToBullet());
        body.WorldTransform = transform;
        body.MotionState?.SetWorldTransform(ref transform);
        SetCompatibilityCubeCenter(body, cube, center);

        // A bitwise test rather than Enum.HasFlag, which boxes both enums in unoptimized builds.
        if ((body.CollisionFlags & CollisionFlags.KinematicObject) == 0)
        {
            body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
            body.AngularVelocity = BulletSharp.Math.Vector3.Zero;
        }

        body.Activate(true);
        ReindexCompatibilityCubeBody(cube, oldCellKey, newCellKey);
        return true;
    }

    private static int IndexOfCompatibilityCubeBody(List<RigidBody> bodies, Cube cube)
    {
        for (int i = 0; i < bodies.Count; i++)
        {
            if (IsCompatibilityCubeBody(bodies[i], cube))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsCompatibilityCubeBody(RigidBody body, Cube cube)
    {
        return body.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
            ReferenceEquals(cubeBody.Item1, cube);
    }

    // Assigning a (Cube, Vector3) tuple to UserObject boxes it on every move. Every body gets its
    // own box when it is created (AddGeometryBody, AddCubeBody, PhysicsSyncSystem), and every
    // reader pattern-matches it into a copy, so the body's existing box is updated in place: the
    // stored type and value are exactly what a fresh assignment would give.
    private static void SetCompatibilityCubeCenter(RigidBody body, Cube cube, Vector3 center)
    {
        object? userObject = body.UserObject;
        if (userObject is ValueTuple<Cube, Vector3> stored && ReferenceEquals(stored.Item1, cube))
        {
            Unsafe.Unbox<ValueTuple<Cube, Vector3>>(userObject).Item2 = center;
            return;
        }

        body.UserObject = (cube, center);
    }

    private List<RigidBody> RentCellBodyList()
    {
        return _spareCellBodyLists.TryPop(out List<RigidBody>? list) ? list : [];
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

        bool isReplicated = !isStatic && _replicatedCubeBodies;
        float mass = isStatic || isReplicated ? 0f : 1f;
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

        if (isReplicated)
        {
            ConfigureReplicatedBody(body);
        }

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);
        if (!isStatic && !isReplicated)
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

        // Fracture pieces stay locally simulated on mirrors because their motion is not replicated yet.
        bool isReplicated = !descriptor.IsStatic &&
            _replicatedCubeBodies &&
            descriptor.CompatibilityCube != null;
        bool isSimulated = !descriptor.IsStatic && !isReplicated;
        float mass = isSimulated ? 1f : 0f;
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
        if (isSimulated)
        {
            ConfigureDynamicBody(body, descriptor.HalfExtents);
        }
        else if (isReplicated)
        {
            ConfigureReplicatedBody(body);
        }

        if (descriptor.FracturePieceIndex.HasValue)
        {
            _physics.AddDebrisBody(body);
        }
        else
        {
            _physics.World.AddRigidBody(body);
        }

        _physics.MapCubeBodies.Add(body);
        if (isSimulated)
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

    private static void ConfigureReplicatedBody(RigidBody body)
    {
        body.CollisionFlags |= CollisionFlags.KinematicObject;
        body.ActivationState = ActivationState.DisableDeactivation;
    }

    private static void ConfigureDynamicBody(RigidBody body, Vector3 halfExtents)
    {
        float smallestHalfExtent = MathF.Max(0.01f, MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z)));
        body.ActivationState = ActivationState.ActiveTag;
        body.ContactProcessingThreshold = smallestHalfExtent * 0.5f;
        body.CcdMotionThreshold = smallestHalfExtent;
        body.CcdSweptSphereRadius = smallestHalfExtent * 0.45f;
        body.SetDamping(0.15f, 0.85f);
        body.SetSleepingThresholds(0.05f, 0.05f);
        body.Activate(true);
    }
}
