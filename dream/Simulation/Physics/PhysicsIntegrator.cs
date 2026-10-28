using BulletSharp;
using BulletSharp.Math;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dream.Simulation.World;

namespace Dream.Simulation.Physics;


public enum PhysicsBodyOwnership
{
    CallerOwned,
    IntegratorOwned
}

public class PhysicsIntegrator : IDisposable
{
    public DiscreteDynamicsWorld World { get; }
    public RigidBody? PlayerBody { get; private set; }
    public RigidBody? FallingCubeBody { get; }
    public List<RigidBody> MapCubeBodies { get; } = new();
    public List<RigidBody> DynamicMapCubeBodies { get; } = new();

    // Fracture rubble is simulated per client and never replicated, so players do not collide
    // with it on either side; otherwise local rubble would make prediction diverge from the server.
    public const CollisionFilterGroups PlayerCollisionGroup = CollisionFilterGroups.CharacterFilter;
    public const CollisionFilterGroups PlayerCollisionMask = CollisionFilterGroups.AllFilter & ~CollisionFilterGroups.DebrisFilter;
    public const CollisionFilterGroups DebrisCollisionGroup = CollisionFilterGroups.DebrisFilter;
    public const CollisionFilterGroups DebrisCollisionMask = CollisionFilterGroups.AllFilter & ~CollisionFilterGroups.CharacterFilter;

    public void AddDebrisBody(RigidBody body)
    {
        World.AddRigidBody(body, DebrisCollisionGroup, DebrisCollisionMask);
    }

    // Bodies of additional server-side session players (every session beyond the primary seat).
    private readonly List<RigidBody> _sessionPlayerBodies = new();

    public IReadOnlyList<RigidBody> SessionPlayerBodies => _sessionPlayerBodies;

    // True while the primary player body is removed from the world (its seat is empty).
    public bool IsPlayerBodyParked { get; private set; }

    private readonly DefaultCollisionConfiguration _collisionConfig;
    private readonly CollisionDispatcher _dispatcher;
    private readonly BroadphaseInterface _broadphase;
    private readonly SequentialImpulseConstraintSolver _solver;
    private PhysicsBodyOwnership? _playerBodyOwnership;
    private readonly Action<RigidBody> _addRigidBodyToWorld;
    private readonly Action<RigidBody> _addPlayerBodyToWorld;
    private readonly Action<RigidBody> _removeRigidBodyFromWorld;
    private readonly Action<RigidBody> _disposeRigidBody;

    // Native teardown safety. BulletSharp 0.11.1 gives every wrapper a finalizer that calls the
    // native delete and ignores the disposing flag. Only the broadphase and the dispatcher put off
    // their delete while a world still uses them; CollisionConfiguration does not. So when an
    // undisposed integrator became garbage, the finalizer thread could delete the configuration,
    // and with it the pair and manifold pool allocators, before CollisionWorld's finalizer ran
    // btCollisionWorld_delete. That delete then freed pair algorithms into the freed pools: an
    // 0xC0000005, or a corrupted native heap that another live world trips over later. The
    // constructor therefore takes the world infrastructure (world, solver, broadphase, dispatcher,
    // configuration) off the finalizer queue. Dispose() still deletes all of it deterministically,
    // in native order. An integrator that is never disposed leaks that native memory on purpose
    // instead of crashing the process. Bodies, shapes and motion states keep their finalizers: a
    // body still in a world only clears its broadphase handle, and a shape or motion state is
    // collected only once nothing that can still step references it.
    //
    // A leak is a bug in the owner, so leaks are tracked. LiveCount counts integrators that were
    // built and not disposed. With TrackLeaks on, a weak registry keeps each creation stack, so
    // TakeCollectedUndisposedCreationStacks can name integrators that became garbage undisposed.
    // (The dream assembly declares no finalizers, so this cannot be a finalizer.)
    private sealed class LeakRecord(PhysicsIntegrator owner, string creationStack)
    {
        public WeakReference<PhysicsIntegrator> Owner { get; } = new(owner);

        public string CreationStack { get; } = creationStack;

        public volatile bool Disposed;
    }

    private static readonly ConcurrentDictionary<LeakRecord, byte> s_trackedRecords = new();
    private static long s_liveCount;

    // Integrators with a native world that were constructed and not yet disposed, process-wide.
    public static long LiveCount => Interlocked.Read(ref s_liveCount);

    // Off by default: one stack trace per construction is too costly for production. Tests, and
    // the DREAM_PHYSICS_LEAK_TRACKING=1 environment variable, turn it on to find a leaking owner.
    public static bool TrackLeaks { get; set; } =
        Environment.GetEnvironmentVariable("DREAM_PHYSICS_LEAK_TRACKING") == "1";

    private readonly LeakRecord? _leakRecord;
    private bool _disposed;

    public PhysicsIntegrator()
    {
        _collisionConfig = new DefaultCollisionConfiguration();
        _dispatcher = new CollisionDispatcher(_collisionConfig);
        _broadphase = new DbvtBroadphase();
        _solver = new SequentialImpulseConstraintSolver();
        World = new DiscreteDynamicsWorld(_dispatcher, _broadphase, _solver, _collisionConfig);
        GC.SuppressFinalize(World);
        GC.SuppressFinalize(_solver);
        GC.SuppressFinalize(_broadphase);
        GC.SuppressFinalize(_dispatcher);
        GC.SuppressFinalize(_collisionConfig);
        Interlocked.Increment(ref s_liveCount);
        if (TrackLeaks)
        {
            _leakRecord = new LeakRecord(this, Environment.StackTrace);
            s_trackedRecords.TryAdd(_leakRecord, 0);
        }

        World.Gravity = new BulletSharp.Math.Vector3(0, -9.8f, 0);
        World.SolverInfo.NumIterations = 10;
        World.SolverInfo.SolverMode = SolverModes.UseWarmStarting;
        World.DispatchInfo.UseContinuous = true;
        _addRigidBodyToWorld = World.AddRigidBody;
        _addPlayerBodyToWorld = body => World.AddRigidBody(body, PlayerCollisionGroup, PlayerCollisionMask);
        _removeRigidBodyFromWorld = World.RemoveRigidBody;
        _disposeRigidBody = DisposeBody;
    }

    internal PhysicsIntegrator(
        Action<RigidBody> addRigidBodyToWorld,
        Action<RigidBody> removeRigidBodyFromWorld,
        Action<RigidBody> disposeRigidBody)
    {
        ArgumentNullException.ThrowIfNull(addRigidBodyToWorld);
        ArgumentNullException.ThrowIfNull(removeRigidBodyFromWorld);
        ArgumentNullException.ThrowIfNull(disposeRigidBody);

        World = null!;
        _collisionConfig = null!;
        _dispatcher = null!;
        _broadphase = null!;
        _solver = null!;
        _addRigidBodyToWorld = addRigidBodyToWorld;
        _addPlayerBodyToWorld = addRigidBodyToWorld;
        _removeRigidBodyFromWorld = removeRigidBodyFromWorld;
        _disposeRigidBody = disposeRigidBody;
        // No native world here, so there is nothing to delete or to track.
        _disposed = true;
    }

    // Returns, and stops tracking, the creation stacks of tracked integrators that the GC has
    // collected without Dispose. Run GC.Collect and WaitForPendingFinalizers first. Integrators
    // that are still reachable are not reported.
    public static IReadOnlyList<string> TakeCollectedUndisposedCreationStacks()
    {
        List<string> leaked = new();
        foreach (LeakRecord record in s_trackedRecords.Keys)
        {
            if (record.Disposed)
            {
                s_trackedRecords.TryRemove(record, out _);
            }
            else if (!record.Owner.TryGetTarget(out _))
            {
                s_trackedRecords.TryRemove(record, out _);
                leaked.Add(record.CreationStack);
            }
        }

        return leaked;
    }

    public void StepSimulation(float deltaTime)
    {
        World.StepSimulation(deltaTime, 10);
    }

    public void StepSimulationFixed(float fixedDeltaTime)
    {
        World.StepSimulation(fixedDeltaTime, 0);
    }

    public void RegisterPlayerBody(RigidBody playerBody, PhysicsBodyOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(playerBody);

        if (PlayerBody != null)
        {
            throw new InvalidOperationException("A player body is already registered with this physics integrator.");
        }

        PlayerBody = playerBody;
        _playerBodyOwnership = ownership;
        _addPlayerBodyToWorld(playerBody);
    }

    public bool IsPlayerBody(RigidBody body)
    {
        return ReferenceEquals(PlayerBody, body) || _sessionPlayerBodies.Contains(body);
    }

    public void AddSessionPlayerBody(RigidBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _sessionPlayerBodies.Add(body);
        _addPlayerBodyToWorld(body);
    }

    public void RemoveSessionPlayerBody(RigidBody body)
    {
        if (!_sessionPlayerBodies.Remove(body))
        {
            return;
        }

        _removeRigidBodyFromWorld(body);
        _disposeRigidBody(body);
    }

    // Takes the primary body out of the world while no session occupies its seat, so it does not
    // leave an invisible collider where the last occupant stood.
    public void SetPlayerBodyParked(bool parked)
    {
        if (PlayerBody == null || parked == IsPlayerBodyParked)
        {
            return;
        }

        if (parked)
        {
            _removeRigidBodyFromWorld(PlayerBody);
        }
        else
        {
            _addPlayerBodyToWorld(PlayerBody);
        }

        IsPlayerBodyParked = parked;
    }

    public RigidBody DetachPlayerBody()
    {
        if (PlayerBody == null)
        {
            throw new InvalidOperationException("No player body is currently registered with this physics integrator.");
        }

        RigidBody playerBody = PlayerBody;
        _removeRigidBodyFromWorld(playerBody);
        PlayerBody = null;
        _playerBodyOwnership = null;
        return playerBody;
    }

    public void ResetMapBodies()
    {
        foreach (RigidBody body in MapCubeBodies)
        {
            if (body == null)
            {
                continue;
            }

            _removeRigidBodyFromWorld(body);
            _disposeRigidBody(body);
        }

        MapCubeBodies.Clear();
        DynamicMapCubeBodies.Clear();
    }

    public void Dispose()
    {
        foreach (RigidBody sessionBody in _sessionPlayerBodies)
        {
            _removeRigidBodyFromWorld(sessionBody);
            _disposeRigidBody(sessionBody);
        }

        _sessionPlayerBodies.Clear();
        if (PlayerBody != null)
        {
            if (!IsPlayerBodyParked)
            {
                _removeRigidBodyFromWorld(PlayerBody);
            }

            if (_playerBodyOwnership == PhysicsBodyOwnership.IntegratorOwned)
            {
                _disposeRigidBody(PlayerBody);
            }

            PlayerBody = null;
            _playerBodyOwnership = null;
        }

        FallingCubeBody?.Dispose();
        ResetMapBodies();
        if (_disposed)
        {
            return;
        }

        // Native order: every body is out of the world above; the world goes before what it uses.
        _disposed = true;
        World.Dispose();
        _solver.Dispose();
        _broadphase.Dispose();
        _dispatcher.Dispose();
        _collisionConfig.Dispose();
        Interlocked.Decrement(ref s_liveCount);
        if (_leakRecord != null)
        {
            _leakRecord.Disposed = true;
            s_trackedRecords.TryRemove(_leakRecord, out _);
        }
    }

    private static void DisposeBody(RigidBody body)
    {
        body.MotionState?.Dispose();
        body.CollisionShape?.Dispose();
        body.Dispose();
    }
}
