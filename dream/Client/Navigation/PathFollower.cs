using OpenTK.Mathematics;

namespace Dream.Client.Navigation;

public readonly record struct NavStep(Vector3 Direction, bool Jump, bool Arrived, bool NeedsReplan);

public sealed class PathFollower
{
    public const float AdvanceDistance = 0.05f;
    public const float TurnAdvanceDistance = 0.12f;
    public const float JumpTriggerDistance = 3.1f;
    public const int JumpLookAheadWaypoints = 4;
    public const float LineGain = 9f;
    public const int PinnedTicksBeforeNudge = 3;
    public const float MinNudgeCorrection = 0.02f;
    public const float PinnedStepMetres = 0.02f;
    public const float MaxLineCorrection = 1f;
    public const int JumpCooldownTicks = 8;
    public const int StuckWindowTicks = 32;
    public const float StuckProgressMetres = 0.05f;
    public const int MaxFailedReplans = 3;

    private readonly List<NavCell> _path = [];
    private int _index;
    private int _jumpCooldown;
    private int _windowTicks;
    private Vector3 _windowStart;
    private bool _jumpTried;
    private int _failedReplans;
    private int _pinnedTicks;
    private Vector3 _lastBody;
    private bool _hasLastBody;

    public bool HasPath => _index < _path.Count;

    // True once the stuck logic has used up its replans. Cleared by Clear and by a fresh path.
    public bool GaveUp { get; private set; }

    // viaStartCentre marks a stuck replan: the start cell centre is a waypoint and the failure
    // count carries on. A normal path skips the start centre and resets the count.
    public void SetPath(IReadOnlyList<NavCell> path, bool viaStartCentre)
    {
        _path.Clear();
        _path.AddRange(path);
        _index = viaStartCentre ? 0 : Math.Min(1, _path.Count);
        _windowTicks = 0;
        _jumpTried = false;
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
        GaveUp = false;
    }

    // Once per 64 Hz tick.
    public NavStep Step(Vector3 bodyCentre, INavWorld world)
    {
        while (HasPath && ReachedWaypoint(bodyCentre))
        {
            _index++;
            _windowTicks = 0;
            _jumpTried = false;
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

        // Moving at full speed covers 0.094 m a tick, so almost none means something is in the way.
        _pinnedTicks = _hasLastBody && HorizontalDistance(bodyCentre, _lastBody) < PinnedStepMetres ? _pinnedTicks + 1 : 0;
        _lastBody = bodyCentre;
        _hasLastBody = true;

        Vector3 toTarget = target.Centre - bodyCentre;
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
        else if (ClimbAhead(bodyCentre))
        {
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
                _failedReplans = 0;
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
                if (++_failedReplans >= MaxFailedReplans)
                {
                    GaveUp = true;
                    _path.Clear();
                    _index = 0;
                    return new NavStep(Vector3.Zero, false, true, false);
                }

                return new NavStep(Vector3.Zero, false, false, true);
            }
        }

        return new NavStep(direction, jump, false, false);
    }

    // A waypoint where the path turns must be reached from the far side (at or past the centre
    // along the incoming direction): arriving short leaves the body overlapping the corner it is
    // about to hug, and it pins there. Straight waypoints only need to be close.
    private bool ReachedWaypoint(Vector3 bodyCentre)
    {
        NavCell target = _path[_index];
        float distance = HorizontalDistance(bodyCentre, target.Centre);
        if (distance < AdvanceDistance)
        {
            return true;
        }

        if (_index == 0 || _index + 1 >= _path.Count || distance >= TurnAdvanceDistance)
        {
            return false;
        }

        NavCell previous = _path[_index - 1];
        NavCell next = _path[_index + 1];
        int inX = Math.Sign(target.X - previous.X);
        int inZ = Math.Sign(target.Z - previous.Z);
        if (inX == Math.Sign(next.X - target.X) && inZ == Math.Sign(next.Z - target.Z))
        {
            return false;
        }

        return ((bodyCentre.X - target.Centre.X) * inX) + ((bodyCentre.Z - target.Centre.Z) * inZ) >= -0.005f;
    }

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
        if (_pinnedTicks >= PinnedTicksBeforeNudge && MathF.Abs(correction) > MinNudgeCorrection)
        {
            // Pressed into a face or corner, friction eats any sideways speed that comes with a
            // forward push. One tick of pure sideways motion frees it.
            _pinnedTicks = 0;
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
