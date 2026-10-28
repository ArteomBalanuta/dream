using BulletSharp;
using BulletSharp.Math;
using Dream.Simulation.World;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Simulation.Physics;


// Turns resting dynamic map cubes into static colliders, and back into dynamic bodies when
// something pushes them, lands them a rider or takes their support away.
//
// Why: Bullet's island manager (btSimulationIslandManager::findUnions) unites every pair of
// overlapping broadphase proxies whose objects both merge islands, and an island sleeps only when
// none of its bodies is ACTIVE_TAG or DISABLE_DEACTIVATION. Touching cubes overlap (the world
// widens each AABB by the contact breaking threshold), so a carpet of placed cubes is one island,
// and player bodies are DISABLE_DEACTIVATION. A player standing on or touching the carpet merged
// into it and kept every cube in it solving and integrating every tick (wave 8: ~2,250 of 2,400
// bodies in Smoke8, ~9.7 ms per tick, against ~0.4 ms with no players). Even with the player kept
// out of the island, one pushed cube woke the whole carpet. Static objects
// (CF_STATIC_OBJECT, mergesSimulationIslands() == false) never join an island and are never
// woken by Bullet, so a frozen cube costs nothing until this class wakes it.
//
// Rules (all applied by PhysicsIntegrator, before each step, in a fixed order):
//   - Freeze: a placed-cube body that Bullet reports WANTS_DEACTIVATION (its own velocity under
//     the sleeping thresholds for gDeactivationTime, 2 s) or ISLAND_SLEEPING. Velocities are
//     zeroed, mass set to 0 (setMassProps sets CF_STATIC_OBJECT) and the state forced to
//     ISLAND_SLEEPING, so pairs between two frozen cubes are not even narrowphased.
//   - Wake (mass and inertia restored, activated): a frozen cube touched by a mover (an awake
//     cube or a player body moving faster than WakeSpeed) when the mover moves into its side, or
//     when it rides on top of the mover. Cubes the mover stands on are supports and stay frozen;
//     so standing, walking on and landing on a carpet wake nothing. Each woken cube passes the
//     push on with the mover's velocity, so a pushed row or stack wakes in the same tick.
//   - Wake: every frozen cube resting on a map body that is removed from the world (a broken or
//     moved cube, a rebuilt cell), or on a player body that leaves the world (a disconnect, a
//     parked seat, a detach) or is teleported, so nothing is left floating (WakeRidersOf).
//   - Wake: a frozen body that anything else activated (Activate(true) after a teleport or a
//     reset), so existing callers keep their meaning. If it was moved, the cubes resting on its
//     old place are woken too. Callers that move a support in place (WorldPhysicsBinder's
//     compatibility move) also wake its riders before the move, which covers unfrozen supports.
// Only bodies whose user object is (Cube, Vector3) are frozen: fracture rubble never touches
// players and sleeps in its own islands.
public sealed class CubeRestFreezer
{
    // Set to "0" to turn freezing off (for example to reproduce pre-PHYS-2 simulation goldens).
    public const string EnabledEnvironmentVariable = "DREAM_CUBE_REST_FREEZE";

    // A mover pushes or carries a frozen cube only above this speed. It is above the cubes'
    // sleeping threshold (0.05 m/s) and far below walking speed.
    public const float WakeSpeed = 0.1f;

    // Gap still counted as touching, on top of the distance the mover covers in the step.
    public const float ContactSlop = 0.03f;

    // A cube whose top is at most this far above the mover's bottom supports it (and one whose
    // bottom is at most this far below the mover's top rides on it), rather than touching its side.
    public const float SupportTolerance = 0.1f;

    // A cube is frozen once its own velocity has stayed under its sleeping thresholds this long
    // (Bullet's deactivation time). Bullet itself waits gDeactivationTime (2 s) before
    // WANTS_DEACTIVATION; a cube at rest is static either way, so waiting less only shortens the
    // time a pushed or bumped cube stays in the solver.
    public const float FreezeAfterRestSeconds = 0.5f;

    // Frozen bodies are only ever unit cubes; anything larger is left dynamic.
    private const float MaximumHalfExtent = 0.5f + 1e-3f;

    private const int CellBias = 1 << 20;

    public static bool DefaultEnabled { get; } =
        Environment.GetEnvironmentVariable(EnabledEnvironmentVariable) != "0";

    // Frozen cubes by the cell their center is in (floor of the center). Two cubes cannot share a
    // cell without overlapping; if one would, the second is left dynamic.
    private readonly Dictionary<long, FrozenCube> _byCell = new();
    private readonly Dictionary<RigidBody, long> _cellByBody = new(ReferenceEqualityComparer.Instance);
    private readonly List<RigidBody> _movers = [];
    private readonly List<WakeSource> _wakeQueue = [];
    private readonly List<long> _scanCells = [];

    private bool _enabled = DefaultEnabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (!value)
            {
                UnfreezeAll();
            }
        }
    }

    public int FrozenCount => _cellByBody.Count;

    public long TotalFrozen { get; private set; }

    public long TotalWoken { get; private set; }

    public bool IsFrozen(RigidBody body)
    {
        return _cellByBody.ContainsKey(body);
    }

    // Runs before a step. Freezes resting cubes, wakes externally activated frozen cubes, then
    // lets every mover (awake cubes in set order, then the player bodies in the given order) wake
    // what it pushes or carries during the step.
    internal void BeforeStep(
        InsertionOrderedSet<RigidBody> dynamicBodies,
        RigidBody? primaryPlayerBody,
        IReadOnlyList<RigidBody> sessionPlayerBodies,
        float deltaTime)
    {
        if (!_enabled)
        {
            CountAwakeWithoutFreezing(dynamicBodies);
            return;
        }

        long wokenBefore = TotalWoken;
        int awake = 0;
        int awakeCubes = 0;
        List<RigidBody> movers = _movers;
        movers.Clear();
        foreach (RigidBody body in dynamicBodies)
        {
            ActivationState state = body.ActivationState;
            if (_cellByBody.Count > 0 && _cellByBody.TryGetValue(body, out long frozenCell))
            {
                if (state != ActivationState.IslandSleeping)
                {
                    UnfreezeActivated(body, frozenCell);
                }

                continue;
            }

            if (state == ActivationState.WantsDeactivation || state == ActivationState.IslandSleeping)
            {
                if (!TryFreeze(body) && state == ActivationState.WantsDeactivation)
                {
                    awake++;
                    awakeCubes += IsCubeBody(body) ? 1 : 0;
                }
            }
            else if (state == ActivationState.ActiveTag)
            {
                if (body.DeactivationTime >= FreezeAfterRestSeconds && TryFreeze(body))
                {
                    continue;
                }

                awake++;
                awakeCubes += IsCubeBody(body) ? 1 : 0;
                if (IsMoving(body.LinearVelocity))
                {
                    movers.Add(body);
                }
            }
        }

        if (_cellByBody.Count == 0)
        {
            movers.Clear();
            PublishAwakeCount(awake, awakeCubes);
            return;
        }

        for (int i = 0; i < movers.Count; i++)
        {
            WakeTouchedBy(movers[i], deltaTime);
        }

        movers.Clear();
        if (primaryPlayerBody != null)
        {
            WakeTouchedBy(primaryPlayerBody, deltaTime);
        }

        for (int i = 0; i < sessionPlayerBodies.Count; i++)
        {
            WakeTouchedBy(sessionPlayerBodies[i], deltaTime);
        }

        int woken = (int)(TotalWoken - wokenBefore);
        PublishAwakeCount(awake + woken, awakeCubes + woken);
    }

    // Diagnostics: the placed-cube and fracture bodies the coming step simulates (awake, or
    // wanting deactivation but still integrated), counted by the last BeforeStep. With freezing
    // off it is the count Bullet would simulate.
    public int AwakeBodyCount { get; private set; }

    // The placed-cube bodies among AwakeBodyCount (the rest are fracture rubble).
    public int AwakeCubeBodyCount { get; private set; }

    // The counts of whichever integrator stepped last in this process. Diagnostics only (the
    // headless load measurement samples them); nothing reads them back.
    public static int LastAwakeBodyCount => Volatile.Read(ref s_lastAwakeBodyCount);

    public static int LastAwakeCubeBodyCount => Volatile.Read(ref s_lastAwakeCubeBodyCount);

    public static int LastFrozenCount => Volatile.Read(ref s_lastFrozenCount);

    private static int s_lastAwakeBodyCount;
    private static int s_lastAwakeCubeBodyCount;
    private static int s_lastFrozenCount;

    private void PublishAwakeCount(int awake, int awakeCubes)
    {
        AwakeBodyCount = awake;
        AwakeCubeBodyCount = awakeCubes;
        Volatile.Write(ref s_lastAwakeBodyCount, awake);
        Volatile.Write(ref s_lastAwakeCubeBodyCount, awakeCubes);
        Volatile.Write(ref s_lastFrozenCount, _cellByBody.Count);
    }

    private static bool IsCubeBody(RigidBody body)
    {
        return body.UserObject is ValueTuple<Cube, TkVector3>;
    }

    private void CountAwakeWithoutFreezing(InsertionOrderedSet<RigidBody> dynamicBodies)
    {
        int awake = 0;
        int awakeCubes = 0;
        foreach (RigidBody body in dynamicBodies)
        {
            ActivationState state = body.ActivationState;
            if (state == ActivationState.ActiveTag || state == ActivationState.WantsDeactivation)
            {
                awake++;
                awakeCubes += IsCubeBody(body) ? 1 : 0;
            }
        }

        PublishAwakeCount(awake, awakeCubes);
    }

    // Wakes what a body moving with its current velocity pushes or carries during the next step.
    public void WakeTouchedBy(RigidBody mover, float deltaTime)
    {
        if (!_enabled || _cellByBody.Count == 0 || !mover.IsInWorld)
        {
            return;
        }

        // A noclip player (no contact response) passes through cubes.
        if ((mover.CollisionFlags & CollisionFlags.NoContactResponse) != 0)
        {
            return;
        }

        Vector3 velocity = mover.LinearVelocity;
        if (!IsMoving(velocity))
        {
            return;
        }

        mover.GetAabb(out Vector3 min, out Vector3 max);
        _wakeQueue.Clear();
        _wakeQueue.Add(new WakeSource(min, max));
        Vector3 displacement = velocity * deltaTime;
        for (int i = 0; i < _wakeQueue.Count; i++)
        {
            WakeSource source = _wakeQueue[i];
            WakeAround(source.Min, source.Max, velocity, displacement);
        }

        _wakeQueue.Clear();
    }

    // Called before a map body leaves the world: drops it if it is frozen, otherwise wakes every
    // frozen cube resting on it (whatever rested on it falls; side neighbours stay frozen).
    internal void OnBodyRemoving(RigidBody body)
    {
        if (_cellByBody.Count == 0)
        {
            return;
        }

        // A frozen body is forgotten without being restored (it is about to be disposed); its
        // neighbours are woken like any removed body's.
        if (_cellByBody.Remove(body, out long cell))
        {
            _byCell.Remove(cell);
        }

        WakeRidersOf(body);
    }

    // Wakes every frozen cube resting on the body where it is now (and the stacks on them). Call
    // it before a body that can carry cubes stops supporting them without moving under them: it
    // leaves the world (a player disconnects, a seat is parked, a body is detached) or is moved
    // in place (a teleport, a server correction, a compatibility move). A teleport zeroes the
    // body's velocity, so the mover rule would never see it carry anything away.
    public void WakeRidersOf(RigidBody body)
    {
        if (!_enabled || _cellByBody.Count == 0 || !body.IsInWorld)
        {
            return;
        }

        body.GetAabb(out Vector3 min, out Vector3 max);
        WakeRidersAbove(min, max);
    }

    // Cubes cannot rotate (angular factor 0), so only the cubes resting on a box, and the stacks
    // on them, lose support when it goes; side neighbours and the cubes below stay frozen.
    private void WakeRidersAbove(Vector3 min, Vector3 max)
    {
        Vector3 slop = new(ContactSlop, ContactSlop, ContactSlop);
        _wakeQueue.Clear();
        _wakeQueue.Add(new WakeSource(min, max));
        for (int i = 0; i < _wakeQueue.Count && _cellByBody.Count > 0; i++)
        {
            WakeSource source = _wakeQueue[i];
            ForEachFrozenNear(source.Min - slop, source.Max + slop, static (freezer, cell, entry, queryMin, queryMax) =>
            {
                if (Overlaps(entry, queryMin, queryMax) &&
                    entry.Min.Y >= queryMax.Y - ContactSlop - SupportTolerance)
                {
                    freezer.Unfreeze(cell);
                    freezer._wakeQueue.Add(new WakeSource(entry.Min, entry.Max));
                }
            });
        }

        _wakeQueue.Clear();
    }

    // Restores every frozen body (turning the freezer off). Bodies are restored in no particular
    // order; restoring changes no world array, so the order has no effect on the simulation.
    public void UnfreezeAll()
    {
        while (_byCell.Count > 0)
        {
            long cell = 0;
            foreach (long key in _byCell.Keys)
            {
                cell = key;
                break;
            }

            Unfreeze(cell);
        }
    }

    // Forgets every frozen body without touching it (the bodies are being disposed).
    internal void Clear()
    {
        _byCell.Clear();
        _cellByBody.Clear();
        _movers.Clear();
        _wakeQueue.Clear();
    }

    private bool TryFreeze(RigidBody body)
    {
        if (body.UserObject is not ValueTuple<Cube, TkVector3>)
        {
            return false;
        }

        if ((body.CollisionFlags & (CollisionFlags.StaticObject | CollisionFlags.KinematicObject | CollisionFlags.NoContactResponse)) != 0)
        {
            return false;
        }

        float inverseMass = body.InvMass;
        if (inverseMass <= 0f)
        {
            return false;
        }

        body.GetAabb(out Vector3 min, out Vector3 max);
        Vector3 halfExtents = (max - min) * 0.5f;
        if (halfExtents.X > MaximumHalfExtent || halfExtents.Y > MaximumHalfExtent || halfExtents.Z > MaximumHalfExtent)
        {
            return false;
        }

        Vector3 center = (min + max) * 0.5f;
        long cell = CellKey(center);
        if (_byCell.ContainsKey(cell))
        {
            return false;
        }

        FrozenCube entry = new(body, min, max, inverseMass, body.LocalInertia);
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;
        body.ClearForces();
        body.SetMassProps(0f, Vector3.Zero);
        body.ForceActivationState(ActivationState.IslandSleeping);
        _byCell.Add(cell, entry);
        _cellByBody.Add(body, cell);
        TotalFrozen++;
        return true;
    }

    // A frozen body that something else activated. A frozen body never moves on its own, so a box
    // that differs from the one it froze with means it was teleported (a sync reset, a map move):
    // the cubes resting on its old place lost their support and are woken too.
    private void UnfreezeActivated(RigidBody body, long cell)
    {
        FrozenCube entry = _byCell[cell];
        Unfreeze(cell);
        body.GetAabb(out Vector3 min, out Vector3 max);
        if (min != entry.Min || max != entry.Max)
        {
            WakeRidersAbove(entry.Min, entry.Max);
        }
    }

    private void Unfreeze(long cell)
    {
        if (!_byCell.Remove(cell, out FrozenCube entry))
        {
            return;
        }

        _cellByBody.Remove(entry.Body);
        RigidBody body = entry.Body;
        body.SetMassProps(1f / entry.InverseMass, entry.LocalInertia);
        body.UpdateInertiaTensor();
        body.Activate(true);
        TotalWoken++;
    }

    // The query box is the mover's box swept over this step's displacement, widened by
    // ContactSlop: the cubes it can touch during the step.
    private void WakeAround(Vector3 moverMin, Vector3 moverMax, Vector3 velocity, Vector3 displacement)
    {
        Vector3 slop = new(ContactSlop, ContactSlop, ContactSlop);
        Vector3 movedMin = moverMin + displacement;
        Vector3 movedMax = moverMax + displacement;
        Vector3 queryMin = Min(moverMin, movedMin) - slop;
        Vector3 queryMax = Max(moverMax, movedMax) + slop;
        int minX = FloorToInt(queryMin.X - 0.5f);
        int maxX = FloorToInt(queryMax.X + 0.5f);
        int minY = FloorToInt(queryMin.Y - 0.5f);
        int maxY = FloorToInt(queryMax.Y + 0.5f);
        int minZ = FloorToInt(queryMin.Z - 0.5f);
        int maxZ = FloorToInt(queryMax.Z + 0.5f);
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    long cell = CellKey(x, y, z);
                    if (!_byCell.TryGetValue(cell, out FrozenCube entry) ||
                        !Overlaps(entry, queryMin, queryMax) ||
                        !IsPushedOrCarried(entry, moverMin, moverMax, movedMin, movedMax, velocity))
                    {
                        continue;
                    }

                    Unfreeze(cell);
                    // The woken cube moves with the mover, so it passes the push on in this tick.
                    _wakeQueue.Add(new WakeSource(entry.Min, entry.Max));
                }
            }
        }
    }

    // Vertical roles come from the mover's current box; the side-contact normal from the box
    // moved by this step's displacement (at the start of a slide along a flush face both
    // horizontal overlaps are about zero, and only the moved box tells the face from the push).
    private static bool IsPushedOrCarried(
        in FrozenCube entry,
        Vector3 moverMin,
        Vector3 moverMax,
        Vector3 movedMin,
        Vector3 movedMax,
        Vector3 velocity)
    {
        if (entry.Max.Y <= moverMin.Y + SupportTolerance)
        {
            // It supports the mover: standing, walking or landing on it needs nothing from it,
            // except at a flush seam (below). Only a support the mover sinks into counts: the
            // snag needs vertical penetration. A mover resting with a positive gap above its
            // supports has no box-box contact with them at all (btBoxBoxDetector reports none
            // for boxes separated along any axis), so the seam is no wall; once gravity settles
            // it in, the next tick's pass sees the penetration and wakes the seam then. Counting
            // a gap within ContactSlop as well was tried: every woken cube passes the push on
            // with this rule, so on cell-aligned stacks it cascaded through whole carpets
            // (Smoke8 awake placed cubes p99 980 against 40).
            return entry.Max.Y > moverMin.Y && IsFlushSeamAhead(entry, moverMin, moverMax, velocity);
        }

        if (entry.Min.Y >= moverMax.Y - SupportTolerance)
        {
            // It rests on the mover, which is moving: it is carried or loses its support.
            return true;
        }

        // A side contact: the normal is the horizontal axis with the smaller overlap.
        float overlapX = MathF.Min(movedMax.X, entry.Max.X) - MathF.Max(movedMin.X, entry.Min.X);
        float overlapZ = MathF.Min(movedMax.Z, entry.Max.Z) - MathF.Max(movedMin.Z, entry.Min.Z);
        float moverCenterX = (moverMin.X + moverMax.X) * 0.5f;
        float moverCenterZ = (moverMin.Z + moverMax.Z) * 0.5f;
        float approach = overlapX <= overlapZ
            ? velocity.X * MathF.Sign(((entry.Min.X + entry.Max.X) * 0.5f) - moverCenterX)
            : velocity.Z * MathF.Sign(((entry.Min.Z + entry.Max.Z) * 0.5f) - moverCenterZ);
        return approach > WakeSpeed;
    }

    // Bullet's box-box detector takes the axis of least penetration as the contact normal. A box
    // resting on a cube (its bottom a hair below the cube's top) whose front face is exactly flush
    // with the next cube's back face penetrates that next cube by 0 along the motion and by the
    // hair vertically, so the contact normal is horizontal and the next cube is a wall. A moving
    // box crosses each seam with some gap and never hits this, but a box starting exactly aligned
    // (a cell-centered spawn or teleport, a cube resting at its cell center) does. Dynamic cubes
    // yielded to that push (the row ahead slid a little and the box got over); a static one
    // would stop it for good. So a support-level cube exactly flush ahead is woken like a pushed
    // one, and passes the push on along its row as before.
    //
    // "Exactly flush" is up to float rounding of the faces, which grows with the coordinate: one
    // ulp is 2.4e-4 at 2048 and 3.9e-3 at 32768. So the tolerance is the larger of an absolute
    // floor and a few ulps of the coordinates compared.
    private const float FlushTolerance = 1e-4f;

    // Four ulps (float epsilon is 2^-23 relative).
    private const float RelativeFlushTolerance = 4f * 1.1920929e-7f;

    private static float FlushToleranceAt(float a, float b)
    {
        return MathF.Max(FlushTolerance, MathF.Max(MathF.Abs(a), MathF.Abs(b)) * RelativeFlushTolerance);
    }

    private static bool IsFlushSeamAhead(in FrozenCube entry, Vector3 moverMin, Vector3 moverMax, Vector3 velocity)
    {
        return IsFlushAhead(velocity.X, entry.Min.X, entry.Max.X, moverMin.X, moverMax.X,
                entry.Min.Z, entry.Max.Z, moverMin.Z, moverMax.Z) ||
            IsFlushAhead(velocity.Z, entry.Min.Z, entry.Max.Z, moverMin.Z, moverMax.Z,
                entry.Min.X, entry.Max.X, moverMin.X, moverMax.X);
    }

    private static bool IsFlushAhead(
        float speed,
        float entryMin,
        float entryMax,
        float moverMin,
        float moverMax,
        float entryCrossMin,
        float entryCrossMax,
        float moverCrossMin,
        float moverCrossMax)
    {
        if (MathF.Abs(speed) <= WakeSpeed)
        {
            return false;
        }

        float gap = speed > 0f ? entryMin - moverMax : moverMin - entryMax;
        float crossOverlap = MathF.Min(entryCrossMax, moverCrossMax) - MathF.Max(entryCrossMin, moverCrossMin);
        return MathF.Abs(gap) <= FlushToleranceAt(entryMin, entryMax) &&
            crossOverlap >= -FlushToleranceAt(entryCrossMin, entryCrossMax);
    }

    private static bool Overlaps(in FrozenCube entry, Vector3 queryMin, Vector3 queryMax)
    {
        return entry.Min.X <= queryMax.X && entry.Max.X >= queryMin.X &&
            entry.Min.Y <= queryMax.Y && entry.Max.Y >= queryMin.Y &&
            entry.Min.Z <= queryMax.Z && entry.Max.Z >= queryMin.Z;
    }

    private void ForEachFrozenNear(
        Vector3 queryMin,
        Vector3 queryMax,
        Action<CubeRestFreezer, long, FrozenCube, Vector3, Vector3> visit)
    {
        int minX = FloorToInt(queryMin.X - 0.5f);
        int maxX = FloorToInt(queryMax.X + 0.5f);
        int minY = FloorToInt(queryMin.Y - 0.5f);
        int maxY = FloorToInt(queryMax.Y + 0.5f);
        int minZ = FloorToInt(queryMin.Z - 0.5f);
        int maxZ = FloorToInt(queryMax.Z + 0.5f);
        long volume = ((long)(maxX - minX + 1)) * (maxY - minY + 1) * (maxZ - minZ + 1);
        if (volume > _byCell.Count * 4L)
        {
            // A large body (a merged static terrain body): scan the frozen cubes instead. The
            // matches are collected first, in cell order, so the wake order does not depend on
            // dictionary order.
            List<long> cells = _scanCells;
            cells.Clear();
            foreach (KeyValuePair<long, FrozenCube> pair in _byCell)
            {
                if (Overlaps(pair.Value, queryMin, queryMax))
                {
                    cells.Add(pair.Key);
                }
            }

            cells.Sort();
            for (int i = 0; i < cells.Count; i++)
            {
                if (_byCell.TryGetValue(cells[i], out FrozenCube entry))
                {
                    visit(this, cells[i], entry, queryMin, queryMax);
                }
            }

            return;
        }

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    long cell = CellKey(x, y, z);
                    if (_byCell.TryGetValue(cell, out FrozenCube entry))
                    {
                        visit(this, cell, entry, queryMin, queryMax);
                    }
                }
            }
        }
    }

    private static Vector3 Min(Vector3 left, Vector3 right)
    {
        return new Vector3(MathF.Min(left.X, right.X), MathF.Min(left.Y, right.Y), MathF.Min(left.Z, right.Z));
    }

    private static Vector3 Max(Vector3 left, Vector3 right)
    {
        return new Vector3(MathF.Max(left.X, right.X), MathF.Max(left.Y, right.Y), MathF.Max(left.Z, right.Z));
    }

    private static bool IsMoving(Vector3 velocity)
    {
        return velocity.LengthSquared > WakeSpeed * WakeSpeed;
    }

    private static int FloorToInt(float value)
    {
        return (int)MathF.Floor(value);
    }

    private static long CellKey(Vector3 center)
    {
        return CellKey(FloorToInt(center.X), FloorToInt(center.Y), FloorToInt(center.Z));
    }

    private static long CellKey(int x, int y, int z)
    {
        return ((long)(x + CellBias) << 42) | ((long)(y + CellBias) << 21) | (long)(z + CellBias);
    }

    private readonly record struct FrozenCube(
        RigidBody Body,
        Vector3 Min,
        Vector3 Max,
        float InverseMass,
        Vector3 LocalInertia);

    private readonly record struct WakeSource(Vector3 Min, Vector3 Max);
}
