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
    bool RangedMainHand);

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
    bool IsDead);

// The animation priority for one player: die (held) > attack (one-shot; the shooting pose for a
// bow) > walk or sprint (playback speed follows the real speed) > idle. A hit adds a short torso
// flinch and a red flash on top of whatever plays, and leaving the ground adds a leg tuck.
// Holding an item raises the arm that holds it. One instance per player; Update allocates nothing.
public sealed class PlayerAnimationStateMachine
{
    public const string IdleClip = "idle";
    public const string WalkClip = "walk";
    public const string SprintClip = "sprint";
    public const string DieClip = "die";
    public const string MeleeClip = "attack-melee-right";
    public const string ShootClip = "holding-right-shoot";

    // Clip lengths, from the model.
    public const float MeleeSeconds = 0.42f;
    public const float ShootSeconds = 0.20f;
    public const float HitSeconds = 0.32f;

    // Authoritative walking speed; the walk cycle plays at its authored rate there.
    public const float ReferenceWalkSpeed = 6f;
    public const float MovingSpeedThreshold = 0.35f;
    public const float SprintSpeedThreshold = 9f;
    public const float MinPlaybackSpeed = 0.6f;
    public const float MaxPlaybackSpeed = 1.6f;

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

    public bool IsDead => _dead;

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
            return new PlayerAnimationFrame(DieClip, PlayerClipMode.Hold, _oneShotSerial, 1f, 0f, 0f, 0f, 0f, true);
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

        float flinch = _hitLeft > 0f ? MathF.Sin(MathF.PI * (1f - (_hitLeft / HitSeconds))) : 0f;

        if (attacking)
        {
            return new PlayerAnimationFrame(
                _attackClip, PlayerClipMode.OneShot, _oneShotSerial, 1f, flinch, _airTuck, _mainArm, _offArm, false);
        }

        string clip;
        float speed = 1f;
        if (_moving && !inputs.Airborne)
        {
            bool sprint = inputs.HorizontalSpeed >= SprintSpeedThreshold;
            clip = sprint ? SprintClip : WalkClip;
            float reference = sprint ? SprintSpeedThreshold : ReferenceWalkSpeed;
            speed = Math.Clamp(inputs.HorizontalSpeed / reference, MinPlaybackSpeed, MaxPlaybackSpeed);
        }
        else
        {
            clip = IdleClip;
        }

        return new PlayerAnimationFrame(
            clip, PlayerClipMode.Loop, _oneShotSerial, speed, flinch, _airTuck, _mainArm, _offArm, false);
    }
}
