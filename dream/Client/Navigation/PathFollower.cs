using OpenTK.Mathematics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Client.Navigation;

public readonly record struct NavStep(Vector3 Direction, bool Jump, bool Arrived, bool NeedsReplan);

public sealed class PathFollower
{
    public const float AdvanceDistance = 0.05f;
    public const float TurnAdvanceDistance = 0.12f;
    public const float JumpTriggerDistance = 3.1f;
    public const int JumpLookAheadWaypoints = 4;
    public const float LineGain = 9f;
    // A body starting from rest, or turning, genuinely covers almost nothing for the first few
    // ticks now that the motor ramps instead of snapping to full speed. One tick only clears
    // PinnedStepMetres once the body passes PinnedStepMetres x tick rate = 1.28 m/s, which takes
    // RampTicksToClearPinnedStep ticks at GroundAcceleration. The old flat 3-tick window was
    // calibrated to the instant-velocity motor, so with a ramp every single start looked pinned
    // and fired the nudge; the soak lost a fifth of its clicks to the resulting fight.
    private const float PinnedStepSpeed = PinnedStepMetres * SimulationClock.AuthoritativeTicksPerSecond;
    private const int RampTicksToClearPinnedStep = (int)(PinnedStepSpeed
        / (PlayerSimulationTuning.DefaultGroundAcceleration / SimulationClock.AuthoritativeTicksPerSecond)) + 1;
    public const int PinnedTicksBeforeNudge = RampTicksToClearPinnedStep + 3;
    public const float MinNudgeCorrection = 0.02f;
    public const float PinnedStepMetres = 0.02f;
    public const float MaxLineCorrection = 1f;
    public const int JumpCooldownTicks = 8;
    public const int StuckWindowTicks = 32;
    public const float StuckProgressMetres = 0.05f;
    public const int MaxFailedReplans = 3;

    // Pressed against a step, wall friction eats the whole jump. So stop pushing, hop straight up
    // and only walk on once the feet clear the step.
    public const int HopTicks = 40;
    public const float HopClearanceMetres = 0.03f;
    public const float HopNeedMetres = 0.1f;
    public const int MaxHopsWithoutGain = 3;
    public const int DropTicks = 12;
    public const int BackOffTicks = 6;
    // How far the player travels in one authoritative tick at the run cruise. The pinned check
    // compares a line correction against one tick of travel, so this has to follow the cruise
    // speed rather than stay the hand-calibrated 0.094 (which was 6 m/s / 64 Hz).
    public const float CruiseTickMetres =
        PlayerSimulationTuning.DefaultRunSpeed / SimulationClock.AuthoritativeTicksPerSecond;
    private const float PinnedStepLimit = LineGain * CruiseTickMetres;
    public const float DropNeedMetres = 0.15f;
    private const float BodyHalfHeight = 0.5f;

    // Waypoints stand this far off a wall on one side: a body flush with a wall face catches on
    // the next cube of that wall and cannot slide along it.
    public const float WallGapMetres = 0.06f;

    // A stuck replan heads back through the start cell, so only real distance from the last replan
    // counts as getting anywhere; a short walk back must not clear the failures.
    public const float ReplanProgressMetres = 2f;

    private readonly List<NavCell> _path = [];
    private int _index;
    private int _jumpCooldown;
    private int _windowTicks;
    private Vector3 _windowStart;
    private bool _jumpTried;
    private int _failedReplans;
    private Vector3 _lastReplanBody;
    private int _pinnedTicks;
    private Vector3 _lastBody;
    private bool _hasLastBody;
    private int _hopTicks;
    private int _hopsWithoutGain;
    private float _hopBaseFeet;
    private int _dropTicks;
    private int _backOffTicks;

    public bool HasPath => _index < _path.Count;

    // True once the stuck logic has used up its replans. Cleared by Clear and by a fresh path.
    public bool GaveUp { get; private set; }

    // viaStartCentre marks a stuck replan: the start cell centre is a waypoint and the failure
    // count carries on. A normal path skips the start centre and resets the count.
    public void SetPath(IReadOnlyList<NavCell> path, bool viaStartCentre, bool startRelocated = false)
    {
        _path.Clear();
        _path.AddRange(path);
        // A relocated start (body hanging past the map edge) is not under the body, so walk to it first.
        _index = viaStartCentre || startRelocated ? 0 : Math.Min(1, _path.Count);
        _windowTicks = 0;
        _jumpTried = false;
        _hopTicks = 0;
        _hopsWithoutGain = 0;
        _dropTicks = 0;
        _backOffTicks = 0;
        if (!viaStartCentre)
        {
            _failedReplans = 0;
            GaveUp = false;
        }
    }

    public void Clear()
    {
        _path.Clear();
        _index = 0;
        _jumpCooldown = 0;
        _windowTicks = 0;
        _jumpTried = false;
        _failedReplans = 0;
        _hopTicks = 0;
        _hopsWithoutGain = 0;
        _dropTicks = 0;
        _backOffTicks = 0;
        GaveUp = false;
    }

    // Once per 64 Hz tick.
    public NavStep Step(Vector3 bodyCentre, INavWorld world)
    {
        while (HasPath && ReachedWaypoint(bodyCentre, world))
        {
            _index++;
            _windowTicks = 0;
            _jumpTried = false;
            _hopsWithoutGain = 0;
        }

        if (!HasPath)
        {
            return new NavStep(Vector3.Zero, false, true, false);
        }

        NavCell target = _path[_index];
        if (!GridPathfinder.IsStandable(world, target.X, target.Level, target.Z))
        {
            return new NavStep(Vector3.Zero, false, false, true);
        }

        // Moving at the run cruise covers CruiseTickMetres a tick, so almost none means something is
        // in the way -- or that the ramp has not built up yet, which is why the nudge waits for
        // PinnedTicksBeforeNudge of them rather than acting on the first.
        _pinnedTicks = _hasLastBody && HorizontalDistance(bodyCentre, _lastBody) < PinnedStepMetres ? _pinnedTicks + 1 : 0;
        _lastBody = bodyCentre;
        _hasLastBody = true;

        float feet = bodyCentre.Y - BodyHalfHeight;
        if (_hopTicks > 0 && feet >= target.Level + HopClearanceMetres)
        {
            _hopTicks = 0;
        }

        // Pushing on a face in mid-air hangs the body there on friction, so let it fall first.
        if (_dropTicks > 0)
        {
            _dropTicks--;
            _pinnedTicks = 0;
            return new NavStep(Vector3.Zero, false, false, false);
        }

        // Feet at a whole height are on a ledge, about to walk off it, not hanging.
        if (_pinnedTicks >= PinnedTicksBeforeNudge && feet > target.Level + DropNeedMetres && MathF.Abs(feet - MathF.Round(feet)) > 0.03f)
        {
            _dropTicks = DropTicks;
        }

        // Also taken in the air: a body pushing on a face hangs there on friction.
        if (_hopTicks == 0 && feet < target.Level - HopNeedMetres && _pinnedTicks >= PinnedTicksBeforeNudge)
        {
            // Hops that gain no height are counted; past the limit the stuck window takes over.
            if (_hopsWithoutGain == 0 || feet > _hopBaseFeet + HopClearanceMetres)
            {
                _hopBaseFeet = feet;
                _hopsWithoutGain = 0;
            }

            if (_hopsWithoutGain < MaxHopsWithoutGain)
            {
                _hopsWithoutGain++;
                _hopTicks = HopTicks;
            }
        }

        if (_hopTicks > 0)
        {
            // One tick with the button up first, because the motor edge-detects the jump.
            bool hop = _hopTicks == HopTicks - 1;
            _hopTicks--;
            return new NavStep(Vector3.Zero, hop, false, false);
        }

        bool pinned = _pinnedTicks >= PinnedTicksBeforeNudge;
        Vector3 toTarget = WaypointAt(_index, world) - bodyCentre;
        Vector3 direction = new Vector3(toTarget.X, 0f, toTarget.Z).Normalized();
        if (_index > 0)
        {
            direction = HoldLine(_path[_index - 1], target, toTarget, direction);
        }

        bool jump = false;
        if (_jumpCooldown > 0)
        {
            _jumpCooldown--;
        }
        else if (ClimbAhead(bodyCentre) || pinned)
        {
            // Also when pinned: at rest and exactly on a cell boundary, the body catches on the
            // seam between two raised cubes, and leaving the ground frees it.

            jump = true;
            _jumpCooldown = JumpCooldownTicks;
        }

        if (_windowTicks == 0)
        {
            _windowStart = bodyCentre;
        }

        if (++_windowTicks >= StuckWindowTicks)
        {
            _windowTicks = 0;
            if (Vector3.Distance(bodyCentre, _windowStart) >= StuckProgressMetres)
            {
                _jumpTried = false;
            }
            else if (!_jumpTried)
            {
                _jumpTried = true;
                jump = true;
                _jumpCooldown = JumpCooldownTicks;
            }
            else
            {
                _jumpTried = false;
                if (_failedReplans > 0 && Vector3.Distance(bodyCentre, _lastReplanBody) >= ReplanProgressMetres)
                {
                    _failedReplans = 0;
                }

                if (_failedReplans >= MaxFailedReplans)
                {
                    GaveUp = true;
                    _path.Clear();
                    _index = 0;
                    return new NavStep(Vector3.Zero, false, true, false);
                }

                _failedReplans++;
                _lastReplanBody = bodyCentre;
                return new NavStep(Vector3.Zero, false, false, true);
            }
        }

        return new NavStep(direction, jump, false, false);
    }

    // A waypoint where the path turns must be reached from the far side (at or past the centre
    // along the incoming direction): arriving short leaves the body overlapping the corner it is
    // about to hug, and it pins there. Straight waypoints only need to be close.
    private bool ReachedWaypoint(Vector3 bodyCentre, INavWorld world)
    {
        NavCell target = _path[_index];
        Vector3 point = WaypointAt(_index, world);
        float distance = HorizontalDistance(bodyCentre, point);
        if (distance < AdvanceDistance)
        {
            return true;
        }

        if (distance >= TurnAdvanceDistance || _index == 0)
        {
            return false;
        }

        // The last waypoint and straight ones need only be close: a tick step is wider than the
        // tighter radius, so the body could step over it and turn back forever.
        if (_index + 1 >= _path.Count)
        {
            return true;
        }

        NavCell previous = _path[_index - 1];
        NavCell next = _path[_index + 1];
        int inX = Math.Sign(target.X - previous.X);
        int inZ = Math.Sign(target.Z - previous.Z);
        if (inX == Math.Sign(next.X - target.X) && inZ == Math.Sign(next.Z - target.Z))
        {
            return true;
        }

        return ((bodyCentre.X - point.X) * inX) + ((bodyCentre.Z - point.Z) * inZ) >= -0.005f;
    }

    // The cell centre, moved off a wall that stands on one side only. Beside a one-wide gap or a
    // wall on the other side the gap stays at zero, so the body enters the next cell centred.
    private Vector3 WaypointAt(int index, INavWorld world)
    {
        NavCell cell = _path[index];
        Vector3 point = cell.Centre;
        point.X += WallGapMetres * SharedPush(index, world, (-1, 0), (1, 0));
        point.Z += WallGapMetres * SharedPush(index, world, (0, -1), (0, 1));
        return point;
    }

    private int SharedPush(int index, INavWorld world, (int X, int Z) low, (int X, int Z) high)
    {
        int push = Push(_path[index], world, low, high);
        for (int i = Math.Max(index - 1, 0); push != 0 && i <= Math.Min(index + 1, _path.Count - 1); i++)
        {
            NavCell cell = _path[i];
            bool bothSides = Blocked(world, cell, low.X, low.Z) + Blocked(world, cell, high.X, high.Z) == 2;
            if (bothSides || Push(cell, world, low, high) == -push)
            {
                push = 0;
            }
        }

        return push;
    }

    private static int Push(NavCell cell, INavWorld world, (int X, int Z) low, (int X, int Z) high)
        => Blocked(world, cell, low.X, low.Z) - Blocked(world, cell, high.X, high.Z);

    private static int Blocked(INavWorld world, NavCell cell, int dx, int dz)
        => world.IsBlocked(cell.X + dx, cell.Level, cell.Z + dz) || world.IsBlocked(cell.X + dx, cell.Level + 1, cell.Z + dz) ? 1 : 0;

    // On an axis-aligned segment, steer back onto the line through the cell centres. The body has
    // no clearance, so a few centimetres of sideways error pins it against a jamb or corner.
    private Vector3 HoldLine(NavCell previous, NavCell target, Vector3 toTarget, Vector3 direct)
    {
        int dx = Math.Sign(target.X - previous.X);
        int dz = Math.Sign(target.Z - previous.Z);
        if ((dx != 0) == (dz != 0))
        {
            return direct;
        }

        Vector3 along = new(dx, 0f, dz);
        Vector3 side = new(-dz, 0f, dx);
        float correction = Math.Clamp(Vector3.Dot(toTarget, side) * LineGain, -MaxLineCorrection, MaxLineCorrection);
        if (_backOffTicks > 0)
        {
            _backOffTicks--;
            return (-along + (side * correction)).Normalized();
        }

        if (_pinnedTicks >= PinnedTicksBeforeNudge && MathF.Abs(correction) > MinNudgeCorrection)
        {
            // Pressed into a face or corner, friction eats any sideways speed that comes with a
            // forward push. One tick of pure sideways motion frees it, unless the error is under
            // one tick of travel: then it would swing past the line forever, so back off and let
            // the line correction settle before the next try.
            _pinnedTicks = 0;
            if (MathF.Abs(correction) < PinnedStepLimit)
            {
                _backOffTicks = BackOffTicks;
                return (-along + (side * correction)).Normalized();
            }

            return side * MathF.Sign(correction);
        }

        float forward = Vector3.Dot(toTarget, along) >= 0f ? 1f : -1f;
        return (along * forward + side * correction).Normalized();
    }

    // The jump needs a run-up: a body pressed against the face cannot rise past the edge, so look
    // a few waypoints ahead for a higher cell within the trigger distance.
    private bool ClimbAhead(Vector3 bodyCentre)
    {
        int level = NavCell.FromBody(bodyCentre).Level;
        int last = Math.Min(_index + JumpLookAheadWaypoints, _path.Count) - 1;
        for (int i = _index; i <= last; i++)
        {
            if (_path[i].Level > level)
            {
                return HorizontalDistance(bodyCentre, _path[i].Centre) <= JumpTriggerDistance;
            }
        }

        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
