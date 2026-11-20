using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Simulation.Players;

namespace Dream.Client.Input;

// What the aim reads: implemented by the play scene, faked in tests.
public interface ICursorAimHost
{
    // The player's body centre; its height is the ground plane the cursor ray is cut with.
    Vector3 BodyCentre { get; }

    PlayerInteractionMode Mode { get; }

    // False without a camera of last frame or a usable viewport.
    bool TryBuildRay(Vector2 screenPosition, out Vector3 origin, out Vector3 direction);
}

// Turns the cursor into a facing (yaw only, towards the point under the cursor) and the left button
// into a held attack. OnFrame runs once per Update, TryGetFacing once per prediction tick (the body
// moves between frames, the cursor does not).
//
// The aim point is the enemy's own anchor when the cursor is on one, and otherwise a cut of the cursor
// ray with the horizontal plane through the player's body. The plane cut is exact only for a point AT
// the player's own height. Under the isometric camera (50 degrees of pitch) a point h metres above that
// plane draws h / tan(50) = 0.84h metres further along the camera's ground direction, so the ray through
// it meets the plane that far past it and the facing points beside the enemy rather than at it. The
// server's melee swing forgives 0.9 m of lateral miss (EnemyRegistry.DefaultHitRadiusMetres), and the
// displacement eats into that by however much it turns the facing. There is no one worst bearing: while
// the displacement is small the miss peaks with the enemy across the camera's ground direction, and once
// it is comparable to the range it peaks with the enemy along it, because then the facing swings by
// tens of degrees. Measure the case you care about rather than assuming an orientation.
//
// The h that matters is NOT the ground rise: what the player points at is the enemy's combat anchor,
// which stands above its feet. Both cases below are MEASURED in a running client, at the skeleton's
// 1.5 m stand-off (artifacts/captures/aim-flat and aim-step, the aimcheck lines; see
// docs/ledger/work-log/2026-11-20-cursor-aim-parallax.md):
//
//   level ground      anchor 0.250 m above the body plane -> plane cut misses by 0.160 m, still a hit
//   one-block step    anchor 1.250 m above the body plane -> plane cut misses by 1.022 m, a clean MISS
//
// So level ground spends about a sixth of the tolerance and is safe, and a single block of rise puts the
// swing 14% past the radius, missing every time. That is the user's report. A cleave's 120-degree arc
// swallows the same error, which is why skills kept landing on an enemy a normal attack could not touch.
// With the anchor as the aim point both cases measure 0.000 m.
//
// An enemy the cursor is ON needs no plane: the pick that found it is a ray/actor intersection, so its
// anchor is where the enemy actually is and the facing is exact at any height.
public sealed class CursorAimController
{
    // Closer than this to the body the direction is noise, and the facing is left as it was.
    public const float MinAimMetres = 0.25f;

    private readonly ICursorAimHost _host;
    private bool _prevLeft;
    // Whether the window was focused on the previous frame; see OnFrame (BH-INPUT-4). It starts TRUE
    // so the controller needs no warm-up frame: a press on the very first frame it ever sees still
    // counts, and only a frame that really reported "unfocused" arms the guard.
    private bool _wasFocused = true;
    private bool _leftArmed;
    private bool _hasPoint;
    private bool _aimedAtTarget;
    private Vector3 _aimPoint;

    public CursorAimController(ICursorAimHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    // True while the cursor owns facing: walking must not turn the character to its travel direction.
    public bool OwnsFacing => _hasPoint;

    // The left button is held, was pressed over no HUD, and the mode attacks: the raw frame's primary use.
    public bool AttackHeld { get; private set; }

    // Where the cursor is taken to point: an enemy's combat anchor, or the plane cut. Its height is
    // not the player's in the first case, which is why nothing but the flat facing may read it.
    public Vector3 AimPoint => _aimPoint;

    // True while the aim point is an enemy's own anchor rather than the plane cut. Reported in the
    // capture log; the two cases differ only in accuracy, so nothing else should branch on it.
    public bool AimedAtTarget => _aimedAtTarget;

    public void OnFrame(in WorldPointerState pointer, bool allowGameplayInput, bool suppressed) =>
        OnFrame(pointer, allowGameplayInput, suppressed, windowFocused: true);

    // BH-INPUT-4: the click that gives the window the focus back is not a new press. While the window
    // is unfocused SampleWorldPointer forces LeftDown false, so _prevLeft latches to false, and the
    // first focused frame — with the button still physically down — reads a press edge and swings the
    // weapon at whatever the player clicked only to come back to the game. Seeding the previous state
    // from the pointer on the first focused frame skips exactly that one edge, the way
    // UiInputRouter.BeginFrame already does when the UI becomes interactive again. A window that
    // already had the focus is untouched, so an ordinary first click still arms and still attacks.
    //
    // aimPoint is the exact world point the cursor is on when that is known - the combat anchor of the
    // live enemy under it, which the caller has already picked for the hover. Passing it in rather than
    // pulling it from the host is deliberate: it cannot then be a frame out of date, and the fact that
    // the pick happens before the aim is something the compiler enforces.
    public void OnFrame(
        in WorldPointerState pointer,
        bool allowGameplayInput,
        bool suppressed,
        bool windowFocused,
        Vector3? aimPoint = null)
    {
        if (!_wasFocused)
        {
            _prevLeft = pointer.LeftDown;
        }

        _wasFocused = windowFocused;
        bool leftPressed = pointer.LeftDown && !_prevLeft;
        _prevLeft = pointer.LeftDown;

        if (!allowGameplayInput || suppressed)
        {
            _leftArmed = false;
            _hasPoint = false;
            AttackHeld = false;
            return;
        }

        // A press counts only if the press edge itself is over no HUD; a drag that began on the HUD
        // never becomes an attack, however far it travels into the world.
        if (leftPressed)
        {
            _leftArmed = !pointer.OverHud;
        }
        else if (!pointer.LeftDown)
        {
            _leftArmed = false;
        }

        // Left attacks in Combat only: in Building the server's primary use places a block along a
        // flat facing, which would drop blocks in mid-air (placement is the right button).
        AttackHeld = pointer.LeftDown && _leftArmed && _host.Mode == PlayerInteractionMode.Combat;

        _hasPoint = false;
        _aimedAtTarget = false;
        // Combat only, so Building keeps the facing it always had: there the left button places a block
        // rather than swinging, nothing is gained by bending the facing onto an enemy, and the plane cut
        // is the behaviour every building test and the player's own habit are built on.
        //
        // An anchor nearer than MinAimMetres falls THROUGH to the plane cut rather than being taken and
        // then rejected by TryGetFacing: the cut always lands further out than the enemy, so an enemy
        // standing almost on top of the player would otherwise freeze the facing while still claiming
        // to own it, which stops the character turning at all. The gate is measured here, once per
        // frame, and TryGetFacing runs per tick against a body that has moved, so a body closing the
        // last centimetres can still reach that state for a frame - as it always could with the plane
        // cut, which is a fixed point the body walks into. This makes it rare, not impossible.
        if (_host.Mode == PlayerInteractionMode.Combat
            && aimPoint is { } target
            && FlatDistanceToBody(target) >= MinAimMetres)
        {
            _aimPoint = target;
            _hasPoint = true;
            _aimedAtTarget = true;
            return;
        }

        if (_host.TryBuildRay(pointer.Position, out Vector3 origin, out Vector3 direction) && direction.Y < -0.0001f)
        {
            float t = (_host.BodyCentre.Y - origin.Y) / direction.Y;
            if (t > 0f)
            {
                _aimPoint = origin + (direction * t);
                _hasPoint = true;
            }
        }
    }

    private float FlatDistanceToBody(Vector3 point)
    {
        Vector3 body = _host.BodyCentre;
        return new Vector3(point.X - body.X, 0f, point.Z - body.Z).Length;
    }

    // The flat unit direction from the body to the aim point, or false to leave facing alone.
    public bool TryGetFacing(out Vector3 facing)
    {
        facing = default;
        if (!_hasPoint)
        {
            return false;
        }

        Vector3 body = _host.BodyCentre;
        Vector3 flat = new(_aimPoint.X - body.X, 0f, _aimPoint.Z - body.Z);
        if (flat.Length < MinAimMetres)
        {
            return false;
        }

        facing = flat.Normalized();
        return true;
    }
}
