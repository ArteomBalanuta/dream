using Dream.Simulation.Players;

namespace Dream.Rendering.Actors;

public enum PlayerClipMode
{
    // Loops until another clip replaces it.
    Loop,

    // Plays once and returns to the loop that was playing.
    OneShot,

    // Plays once and stays on its last pose (death).
    Hold
}

// The ground gait the body is running, in speed order. Each tier has its own authored cycle, which
// is NOT tied to a simulation speed: the tier is picked from the speed the player is ACTUALLY
// moving at, with hysteresis on both boundaries, and the clip's playback rate absorbs the
// difference from the speed it was authored for.
public enum PlayerGaitTier
{
    Walk,
    Run,
    Sprint
}

// What one player shows this frame, from the facts the renderer has about it. Serials count events
// (an attack swing, a hit taken); the state machine starts an animation when a serial it has not
// seen changes. The first serial it sees only sets the baseline, so a player that comes into view
// does not replay an old swing.
public readonly record struct PlayerAnimationInputs(
    bool IsAlive,
    float HorizontalSpeed,
    bool Airborne,
    int AttackSerial,
    int HitSerial,
    bool HoldsMainHand,
    bool HoldsOffHand,
    bool RangedMainHand,
    // Signed yaw, radians, from where the body faces to where it travels: the RAW delta of
    // contract C2, wrapped to (-pi, pi], as LegTwist computes it. Update picks the walk clip from
    // its sector and passes only the within-sector RESIDUAL to the pose layers.
    float LegTwistRadians = 0f);

public readonly record struct PlayerAnimationFrame(
    string Clip,
    PlayerClipMode Mode,
    // Counts the one-shot starts: a new value is a new swing even though the clip name is the same.
    int OneShotSerial,
    float PlaybackSpeed,
    // 0..1 envelopes of the procedural layers.
    float HitFlinch,
    float AirTuck,
    // How much the held-item arms override the clip: 0 clip-driven, 1 raised to hold the item.
    float MainArmHold,
    float OffArmHold,
    bool IsDead,
    // The leg twist for the pose layers: the RESIDUAL left over after the directional clip (C2),
    // clamped to +-MaxLegTwistRadians; 0 when stationary or dead. Never the raw delta - the clips
    // are authored on this split, and carrying the raw delta here would double-count the legs.
    float LegTwistRadians = 0f);

// The animation priority for one player: die (held) > attack (one-shot; the shooting pose for a
// bow) > the ground gait - walk, run or sprint, picked from the real speed, its playback rate
// following that speed inside the tier > idle. A hit adds a short torso
// flinch and a red flash on top of whatever plays, and leaving the ground adds a leg tuck.
// Holding an item raises the arm that holds it. One instance per player; Update allocates nothing.
public sealed class PlayerAnimationStateMachine
{
    public const string IdleClip = "idle";
    public const string WalkClip = "walk";
    public const string RunClip = "run";
    public const string SprintClip = "sprint";
    public const string DieClip = "die";
    public const string MeleeClip = "attack-melee-right";
    public const string ShootClip = "holding-right-shoot";

    // Directional walk cycles (C2): the body keeps facing the cursor while the stride runs
    // backwards or sideways, so the legs no longer sit at a 90-degree clamp to hide a moonwalk.
    public const string WalkBackClip = "walk-back";
    public const string StrafeLeftClip = "walk-strafe-left";
    public const string StrafeRightClip = "walk-strafe-right";

    // Clip lengths, from the model.
    public const float MeleeSeconds = 0.42f;
    public const float ShootSeconds = 0.20f;
    public const float HitSeconds = 0.32f;

    // The speed each gait's cycle was AUTHORED for: at exactly this speed the clip plays at its
    // authored rate (playback 1.0). These describe the clips, not the simulation: the shipped
    // player speeds sit within the playback clamp of them (the default cruise plays the walk clip a
    // little fast, the Shift sprint plays the run clip a little slow), which
    // PlayerAnimationStateMachineTests pins so the feet never skate. The sprint clip is only
    // reachable above the shipped speeds.
    public const float ReferenceWalkSpeed = 1.6f;
    public const float ReferenceRunSpeed = 4.3f;
    public const float ReferenceSprintSpeed = 6.0f;

    public const float MovingSpeedThreshold = 0.35f;

    // The tier boundaries, between the speeds they separate rather than on top of one of them, so a
    // player cruising at exactly one tier's speed never sits on a boundary. The run clip takes over
    // midway between the default cruise and the Shift sprint; the sprint clip midway between the
    // run and sprint clips' authored speeds.
    public const float RunSpeedThreshold =
        (PlayerSimulationTuning.DefaultRunSpeed + PlayerSimulationTuning.DefaultSprintSpeed) / 2f;
    public const float SprintSpeedThreshold = (ReferenceRunSpeed + ReferenceSprintSpeed) / 2f;

    // Deadband on BOTH tier boundaries, in favour of the tier already playing, for the same reason
    // SectorHysteresisRadians exists: AnimationPlaybackState.Play resets the clip time to 0 when the
    // clip NAME changes, so a speed hovering at a boundary would restart the stride every frame and
    // the legs would stutter instead of walking. Acceleration carries the body across a boundary in
    // far less time than a stride, so the damping costs nothing visible.
    public const float GaitHysteresisSpeed = 0.4f;

    // The playback floor is the walk floor's own rate on the walk clip, so the slowest speed the
    // simulation can produce plays the legs at the body's pace instead of being clamped up past it.
    public const float MinPlaybackSpeed = PlayerSimulationTuning.DefaultWalkSpeed / ReferenceWalkSpeed;
    public const float MaxPlaybackSpeed = 1.6f;

    // The legs twist at most an eighth of a turn off the facing. With the directional clips (C2)
    // the clip covers the 90 and 180 degree cases, so all that is left for the pose layers is the
    // within-sector residual: at most 45 degrees by construction, and the clamp is the guard.
    public const float MaxLegTwistRadians = MathF.PI / 4f;

    // The paths that deliberately play ONE non-directional clip - the sprint, an attack while
    // moving, and the airborne idle - still feed the pose layers the RAW delta, so they keep the
    // wider clamp the whole state machine used before the directional clips existed. Clamping their
    // raw delta to pi/4 would leave a backwards sprint 135 degrees off its travel, which is further
    // from the truth than the 90 degrees it had before this wave.
    public const float MaxSprintLegTwistRadians = MathF.PI / 2f;

    // The sector boundaries of C2's table: |delta| <= 45 degrees walks forward, 45..135 strafes,
    // beyond 135 walks backwards.
    public const float StrafeSectorStartRadians = MathF.PI / 4f;
    public const float BackSectorStartRadians = 3f * MathF.PI / 4f;

    // Deadband on those boundaries, in the shape of the _moving hysteresis below: the sector that is
    // already playing holds on for this much past its boundary. AnimationPlaybackState.Play resets
    // the clip time to 0 whenever the clip NAME changes, and travel is quantised to eight WASD
    // octants while facing is continuous from the cursor, so a cursor sitting near 45 degrees off
    // the key direction - an ordinary configuration - would otherwise flip walk <-> walk-strafe-
    // right frame to frame on mouse jitter and restart the stride at frame 0 each time: the legs
    // stutter instead of walking. A residual may exceed 45 degrees by this much inside the sticky
    // band, which the MaxLegTwistRadians clamp absorbs.
    public const float SectorHysteresisRadians = 5f * MathF.PI / 180f;

    private const float LayerBlendPerSecond = 12f;

    private bool _seeded;
    private int _lastAttackSerial;
    private int _lastHitSerial;
    private int _oneShotSerial;
    private float _attackLeft;
    private string _attackClip = MeleeClip;
    private float _hitLeft;
    private bool _dead;
    private float _airTuck;
    private float _mainArm;
    private float _offArm;
    private bool _moving;
    // The gait tier playing, so ResolveGaitTier can hold it past its boundary.
    private PlayerGaitTier _gait;
    // The directional walk clip picked last, so its sector can hold on past its boundary. Kept
    // across a pause, which only widens the sector a resumed walk starts in by the deadband.
    private string? _walkClip;

    public bool IsDead => _dead;

    // The signed yaw from facing to travel on the ground plane, wrapped to (-pi, pi] and NOT
    // clamped: this is C2's raw delta, which Update needs whole to pick the walk clip's sector. 0
    // when either direction is (nearly) zero. Yaw is atan2(x, z), the same convention as the
    // model's facing, so a positive delta turns the stride toward +X when facing +Z.
    public static float LegTwist(float facingX, float facingZ, float travelX, float travelZ)
    {
        if (((facingX * facingX) + (facingZ * facingZ)) < 1e-8f || ((travelX * travelX) + (travelZ * travelZ)) < 1e-8f)
        {
            return 0f;
        }

        float delta = MathF.Atan2(travelX, travelZ) - MathF.Atan2(facingX, facingZ);
        return MathF.IEEERemainder(delta, MathF.Tau);
    }

    // C2's table: the walk clip for a raw travel-minus-facing delta, and the residual twist the
    // pose layers still have to apply. |residual| is at most 45 degrees in every sector, so the
    // clip carries the direction and the twist only trims what is left inside it.
    public static string ResolveWalkClip(float deltaRadians, out float residualRadians) =>
        ResolveWalkClip(deltaRadians, null, out residualRadians);

    // The same table, with SectorHysteresisRadians of deadband in favour of the clip already
    // playing. currentClip null is the plain table above. Allocates nothing.
    public static string ResolveWalkClip(float deltaRadians, string? currentClip, out float residualRadians)
    {
        float delta = MathF.IEEERemainder(deltaRadians, MathF.Tau);
        float magnitude = MathF.Abs(delta);
        float strafeStart = StrafeSectorStartRadians;
        float backStart = BackSectorStartRadians;
        if (string.Equals(currentClip, WalkClip, StringComparison.Ordinal))
        {
            // Walking forward holds on a few degrees into the strafe sector.
            strafeStart += SectorHysteresisRadians;
        }
        else if (string.Equals(currentClip, WalkBackClip, StringComparison.Ordinal))
        {
            backStart -= SectorHysteresisRadians;
        }
        else if (string.Equals(currentClip, delta > 0f ? StrafeRightClip : StrafeLeftClip, StringComparison.Ordinal))
        {
            // A strafe on this side holds both of its own boundaries. The two strafes cannot fight
            // over the sign: swapping sides means passing through the forward sector.
            strafeStart -= SectorHysteresisRadians;
            backStart += SectorHysteresisRadians;
        }

        if (magnitude <= strafeStart)
        {
            residualRadians = delta;
            return WalkClip;
        }

        if (magnitude <= backStart)
        {
            residualRadians = delta - MathF.CopySign(MathF.PI / 2f, delta);
            return delta > 0f ? StrafeRightClip : StrafeLeftClip;
        }

        residualRadians = delta - MathF.CopySign(MathF.PI, delta);
        return WalkBackClip;
    }

    // The gait tier for a speed, with GaitHysteresisSpeed of deadband in favour of `current`: the
    // tier already playing holds on for that much speed past its boundary, in both directions. Pure
    // and allocation-free.
    public static PlayerGaitTier ResolveGaitTier(float speed, PlayerGaitTier current)
    {
        float runBoundary = RunSpeedThreshold +
            (current == PlayerGaitTier.Walk ? GaitHysteresisSpeed : -GaitHysteresisSpeed);
        float sprintBoundary = SprintSpeedThreshold +
            (current == PlayerGaitTier.Sprint ? -GaitHysteresisSpeed : GaitHysteresisSpeed);
        if (speed >= sprintBoundary)
        {
            return PlayerGaitTier.Sprint;
        }

        return speed >= runBoundary ? PlayerGaitTier.Run : PlayerGaitTier.Walk;
    }

    // The speed the tier's clip is authored for, which the playback rate divides by.
    public static float ReferenceSpeed(PlayerGaitTier tier) => tier switch
    {
        PlayerGaitTier.Sprint => ReferenceSprintSpeed,
        PlayerGaitTier.Run => ReferenceRunSpeed,
        _ => ReferenceWalkSpeed
    };

    // The plain walk cycle stands in for a directional clip a model does not carry: a clip name the
    // skeleton lacks stops playback and freezes the pose, so the fallback keeps the legs moving on a
    // model forged before C2's clips existed. Every other clip is its own fallback.
    public static string FallbackClip(string clipName) =>
        string.Equals(clipName, RunClip, StringComparison.Ordinal) ||
        string.Equals(clipName, WalkBackClip, StringComparison.Ordinal) ||
        string.Equals(clipName, StrafeLeftClip, StringComparison.Ordinal) ||
        string.Equals(clipName, StrafeRightClip, StringComparison.Ordinal)
            ? WalkClip
            : clipName;

    public PlayerAnimationFrame Update(in PlayerAnimationInputs inputs, float deltaSeconds)
    {
        float dt = Math.Clamp(deltaSeconds, 0f, 0.25f);

        if (!_seeded)
        {
            _seeded = true;
            _lastAttackSerial = inputs.AttackSerial;
            _lastHitSerial = inputs.HitSerial;
        }

        if (!inputs.IsAlive)
        {
            if (!_dead)
            {
                _dead = true;
                _attackLeft = 0f;
                _hitLeft = 0f;
                _oneShotSerial++;
            }

            _airTuck = 0f;
            _mainArm = 0f;
            _offArm = 0f;
            _lastAttackSerial = inputs.AttackSerial;
            _lastHitSerial = inputs.HitSerial;
            return new PlayerAnimationFrame(DieClip, PlayerClipMode.Hold, _oneShotSerial, 1f, 0f, 0f, 0f, 0f, true, 0f);
        }

        if (_dead)
        {
            // Respawned: back to a fresh idle.
            _dead = false;
            _attackLeft = 0f;
            _hitLeft = 0f;
            _oneShotSerial++;
        }

        _attackLeft = MathF.Max(0f, _attackLeft - dt);
        _hitLeft = MathF.Max(0f, _hitLeft - dt);

        if (inputs.HitSerial != _lastHitSerial)
        {
            _lastHitSerial = inputs.HitSerial;
            _hitLeft = HitSeconds;
        }

        if (inputs.AttackSerial != _lastAttackSerial)
        {
            _lastAttackSerial = inputs.AttackSerial;
            if (_attackLeft <= 0f)
            {
                _attackClip = inputs.RangedMainHand ? ShootClip : MeleeClip;
                _attackLeft = inputs.RangedMainHand ? ShootSeconds : MeleeSeconds;
                _oneShotSerial++;
            }
        }

        bool attacking = _attackLeft > 0f;

        // Hysteresis: a stop is a lower speed than a start, so a speed that hovers at the threshold
        // does not flicker between the walk and the idle clip.
        _moving = _moving
            ? inputs.HorizontalSpeed > MovingSpeedThreshold * 0.6f
            : inputs.HorizontalSpeed > MovingSpeedThreshold;

        float blend = Math.Min(1f, LayerBlendPerSecond * dt);
        _airTuck += ((inputs.Airborne ? 1f : 0f) - _airTuck) * blend;
        // The swing clip drives the right arm; the shooting pose does too (it keeps the arm raised).
        bool mainArmHeld = inputs.HoldsMainHand && !(attacking && _attackClip == MeleeClip);
        _mainArm += ((mainArmHeld ? 1f : 0f) - _mainArm) * blend;
        _offArm += ((inputs.HoldsOffHand ? 1f : 0f) - _offArm) * blend;

        // Only a moving body twists its hips; a stationary one (or an attacking one standing still)
        // keeps them square. Outside the directional walk this is the raw delta under the WIDER
        // sprint clamp, as the whole state machine did before C2; the walk branch below replaces it
        // with its sector's residual under the narrow one.
        float rawTwist = _moving ? inputs.LegTwistRadians : 0f;
        float legTwist = Math.Clamp(rawTwist, -MaxSprintLegTwistRadians, MaxSprintLegTwistRadians);

        float flinch = _hitLeft > 0f ? MathF.Sin(MathF.PI * (1f - (_hitLeft / HitSeconds))) : 0f;

        if (attacking)
        {
            return new PlayerAnimationFrame(
                _attackClip, PlayerClipMode.OneShot, _oneShotSerial, 1f, flinch, _airTuck, _mainArm, _offArm, false, legTwist);
        }

        string clip;
        float speed = 1f;
        if (_moving && !inputs.Airborne)
        {
            _gait = ResolveGaitTier(inputs.HorizontalSpeed, _gait);
            // EVERY tier goes through C2's sector table, the sprint included. The walk and the run
            // tier need it because the run is the default cruise, so a run that skipped the table
            // would leave the moonwalk fix applying only at speeds the player almost never moves
            // at. The sprint goes through it because you CANNOT SPRINT SIDEWAYS: that branch used
            // to bypass the table and play the forward sprint cycle with the legs yawed up to 90
            // degrees, which was unreachable dead code while SprintSpeedThreshold was 9 m/s
            // against a 6 m/s cap and became reachable in this wave. Nobody has ever looked at a
            // sideways or backwards sprint, so rather than ship a never-inspected pose for a
            // capture to judge, a sprint outside the forward sector falls through to the run
            // tier's handling below and keeps the directional clip.
            //
            // Running the table on every path also means _walkClip is always current: the old
            // sprint branch left it stale, so the sector hysteresis resumed from whatever sector
            // the body walked in before a long sprint.
            string sector = ResolveWalkClip(rawTwist, _walkClip, out float residual);
            _walkClip = sector;
            bool forwardSector = string.Equals(sector, WalkClip, StringComparison.Ordinal);
            float reference;
            if (_gait == PlayerGaitTier.Sprint && forwardSector)
            {
                // One sprint clip for the whole forward sector, so the twist stays the RAW delta
                // under the wider sprint clamp (legTwist as computed above) - the clip carries no
                // direction of its own, so the pose layers must carry all of it.
                clip = SprintClip;
                reference = ReferenceSprintSpeed;
            }
            else
            {
                // The forward sector swaps in the tier's own cycle; the other three reuse the
                // directional walk clips. Those three are walk-AUTHORED and unchanged by this
                // wave, so their playback divides by the WALK reference and not by the tier's: at
                // the shipped sprint the tier's own reference plays the clip near its authored rate
                // and the feet skate, while the walk reference clamps at MaxPlaybackSpeed. A sprint that fell through to here lands on the same clamp.
                clip = forwardSector && _gait == PlayerGaitTier.Run ? RunClip : sector;
                reference = forwardSector ? ReferenceSpeed(_gait) : ReferenceWalkSpeed;
                // A directional clip carries its sector, so the pose layers get only the RESIDUAL
                // under the narrow clamp. Passing the raw delta here would double-count the turn:
                // it looks right from the front and wrong from the side.
                legTwist = Math.Clamp(residual, -MaxLegTwistRadians, MaxLegTwistRadians);
            }

            speed = Math.Clamp(inputs.HorizontalSpeed / reference, MinPlaybackSpeed, MaxPlaybackSpeed);
        }
        else
        {
            clip = IdleClip;
        }

        return new PlayerAnimationFrame(
            clip, PlayerClipMode.Loop, _oneShotSerial, speed, flinch, _airTuck, _mainArm, _offArm, false, legTwist);
    }
}
