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

// Turns the cursor into a facing (yaw only, towards the cursor's ground point) and the left button
// into a held attack. OnFrame runs once per Update, TryGetFacing once per prediction tick (the body
// moves between frames, the cursor does not). No world pick: a ray/plane cut is all a facing needs.
public sealed class CursorAimController
{
    // Closer than this to the body the direction is noise, and the facing is left as it was.
    public const float MinAimMetres = 0.25f;

    private readonly ICursorAimHost _host;
    private bool _prevLeft;
    private bool _leftArmed;
    private bool _hasPoint;
    private Vector3 _groundPoint;

    public CursorAimController(ICursorAimHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    // True while the cursor owns facing: walking must not turn the character to its travel direction.
    public bool OwnsFacing => _hasPoint;

    // The left button is held, was pressed over no HUD, and the mode attacks: the raw frame's primary use.
    public bool AttackHeld { get; private set; }

    public Vector3 GroundPoint => _groundPoint;

    public void OnFrame(in WorldPointerState pointer, bool allowGameplayInput, bool suppressed)
    {
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
        if (_host.TryBuildRay(pointer.Position, out Vector3 origin, out Vector3 direction) && direction.Y < -0.0001f)
        {
            float t = (_host.BodyCentre.Y - origin.Y) / direction.Y;
            if (t > 0f)
            {
                _groundPoint = origin + (direction * t);
                _hasPoint = true;
            }
        }
    }

    // The flat unit direction from the body to the cursor's ground point, or false to leave facing alone.
    public bool TryGetFacing(out Vector3 facing)
    {
        facing = default;
        if (!_hasPoint)
        {
            return false;
        }

        Vector3 body = _host.BodyCentre;
        Vector3 flat = new(_groundPoint.X - body.X, 0f, _groundPoint.Z - body.Z);
        if (flat.Length < MinAimMetres)
        {
            return false;
        }

        facing = flat.Normalized();
        return true;
    }
}
