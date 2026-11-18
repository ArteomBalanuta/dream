using OpenTK.Mathematics;
using Dream.Client.Navigation;
using Dream.Rendering.Core;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;

namespace Dream.Client.Input;

public enum ClickGoalKind : byte
{
    None,
    Walk,
    Attack,
    Build
}

public enum ClickPickKind : byte
{
    None,
    Ground,
    Enemy
}

// A live enemy as the controller sees it.
public interface IClickTarget
{
    bool IsAlive { get; }

    // The point the attack is aimed at (the combat anchor).
    Vector3 Anchor { get; }
}

// What a cursor pick found. Ground: Cell is the last empty cell before the first blocked one and
// Point the surface point. Enemy: Target is set and Point is its anchor.
public readonly record struct ClickPick(ClickPickKind Kind, NavCell Cell, Vector3 Point, IClickTarget? Target);

// The world the controller reads and acts on: implemented by the play scene, faked in tests.
public interface IClickMoveHost
{
    INavWorld? World { get; }

    Vector3 BodyCentre { get; }

    Vector3 Eye { get; }

    bool NoclipActive { get; }

    PlayerInteractionMode Mode { get; }

    // False without a camera of last frame, or when the cursor points at nothing.
    bool TryPick(Vector2 screenPosition, out ClickPick pick);

    void SetFacing(Vector3 direction);

    // The character's current facing (the resolver's movement basis).
    Vector3 Facing { get; }

    // True while the cursor owns facing: a walk then must not turn the character to its travel
    // direction, and sends the axes that move it that way from the facing it has.
    bool AimOwnsFacing { get; }
}

// A world that can say whether a straight line is free of walls. Optional: a world without it is
// treated as open, so only the real map gates attacks on line of sight.
public interface ISightWorld
{
    bool HasClearLine(Vector3 origin, Vector3 anchor);
}

// The path search and follower behind one seam.
public interface IClickPathing
{
    bool HasPath { get; }

    // True when Arrived from the last Step means the stuck replans ran out, not that the goal was reached.
    bool GaveUp { get; }

    // Cells of the last planned path, for the capture log.
    int LastPathCells { get; }

    // False only when there is no standable start.
    bool Plan(INavWorld world, NavCell start, NavCell goal, bool viaStartCentre, out bool reachedGoal);

    void Clear();

    NavStep Step(Vector3 bodyCentre, INavWorld world);
}

public sealed class NavPathing : IClickPathing
{
    private readonly GridPathfinder _finder = new();
    private readonly PathFollower _follower = new();
    private readonly List<NavCell> _path = new();

    public bool HasPath => _follower.HasPath;

    public bool GaveUp => _follower.GaveUp;

    public int LastPathCells => _path.Count;

    public bool Plan(INavWorld world, NavCell start, NavCell goal, bool viaStartCentre, out bool reachedGoal)
    {
        _path.Clear();
        if (!_finder.TryFindPath(world, start, goal, _path, out reachedGoal))
        {
            _follower.Clear();
            return false;
        }

        _follower.SetPath(_path, viaStartCentre, _finder.StartRelocated);
        return true;
    }

    public void Clear() => _follower.Clear();

    public NavStep Step(Vector3 bodyCentre, INavWorld world) => _follower.Step(bodyCentre, world);
}

// Turns right-button presses into a goal (walk, attack, build) and the goal into one steered command per
// 64 Hz prediction tick. OnFrame runs once per Update, SteerTick once per tick.
public sealed class ClickMoveController
{
    public const float BuildStartWalkMetres = 2.5f;
    public const float BuildStopMetres = 2.4f;
    public const float EnemyRepathMetres = 1f;
    public const int EnemyRepathTicks = 32;
    public const int HeldRepathTicks = 16;

    private readonly IClickMoveHost _host;
    private readonly IClickPathing _pathing;
    private bool _prevRight;
    // Whether the window was focused on the previous frame; see OnFrame (BH-INPUT-4). It starts TRUE
    // so the controller needs no warm-up frame: a press on the very first frame it ever sees still
    // counts, and only a frame that really reported "unfocused" arms the guard.
    private bool _wasFocused = true;
    private bool _rightArmed;
    private ClickGoalKind _goal;
    private NavCell _goalCell;
    private IClickTarget? _target;
    private Vector3 _plannedAnchor;
    private bool _modeSent;
    private bool _buildWalking;
    private bool _buildPulsed;
    private int _ticksSincePlan;
    private int _ticksSinceHeldPick;

    public ClickMoveController(IClickMoveHost host, IClickPathing pathing)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _pathing = pathing ?? throw new ArgumentNullException(nameof(pathing));
    }

    public ClickGoalKind Goal => _goal;

    // The enemy of an attack goal, for the capture log.
    public IClickTarget? Target => _target;

    public NavCell GoalCell => _goalCell;

    public int LastPathCells => _pathing.LastPathCells;

    // True while the goal needs the character to walk.
    public bool IsWalking => _goal != ClickGoalKind.None && _pathing.HasPath && !InAttackRange() && !InBuildRange();

    public void Cancel()
    {
        _goal = ClickGoalKind.None;
        _target = null;
        _buildWalking = false;
        _pathing.Clear();
    }

    // Frame level: press edges, picks and paths. The left button is the cursor aim's (a held attack);
    // the right button opens goals here.
    public void OnFrame(in WorldPointerState pointer, bool allowGameplayInput) =>
        OnFrame(pointer, allowGameplayInput, windowFocused: true);

    // BH-INPUT-4: the click that gives the window the focus back is not a new press. While the window
    // is unfocused SampleWorldPointer forces RightDown false, so _prevRight latches to false, and the
    // first focused frame — with the button still physically down — would read a press edge and send
    // a click-to-move / attack goal at the point the player clicked only to come back to the game.
    // Seeding the previous state from the pointer on the first focused frame skips exactly that one
    // edge, the way UiInputRouter.BeginFrame already does when the UI becomes interactive again. A
    // window that already had the focus is untouched, so an ordinary first click still counts.
    public void OnFrame(in WorldPointerState pointer, bool allowGameplayInput, bool windowFocused)
    {
        if (!_wasFocused)
        {
            _prevRight = pointer.RightDown;
        }

        _wasFocused = windowFocused;
        bool rightPressed = pointer.RightDown && !_prevRight;
        _prevRight = pointer.RightDown;

        if (!allowGameplayInput)
        {
            _rightArmed = false;
            Cancel();
            return;
        }

        // A press counts only if the press edge itself is over no HUD; a drag that began on the HUD
        // never becomes a world click.
        if (rightPressed)
        {
            _rightArmed = !pointer.OverHud;
        }
        else if (!pointer.RightDown)
        {
            _rightArmed = false;
        }

        if (_host.NoclipActive)
        {
            // Noclip flies by hand: a goal still under way would keep steering the flight.
            Cancel();
            return;
        }

        if (rightPressed && _rightArmed)
        {
            OnRightPress(pointer.Position);
        }
        else if (pointer.RightDown && _rightArmed && _host.Mode == PlayerInteractionMode.Combat
            && _goal is ClickGoalKind.Walk or ClickGoalKind.None && _ticksSinceHeldPick >= HeldRepathTicks)
        {
            RepathToCursor(pointer.Position);
        }
    }

    // Marks the raw frame as walking, so a send stall that zeroes the axes can be told apart.
    public PlayerCommandFrame MarkFrame(PlayerCommandFrame frame)
    {
        if (!IsWalking)
        {
            return frame;
        }

        return WithForward(frame, 1f);
    }

    // Tick level, first line of the prediction tick.
    public PlayerCommandFrame SteerTick(PlayerCommandFrame command)
    {
        _ticksSincePlan++;
        _ticksSinceHeldPick++;
        INavWorld? world = _host.World;
        if (world is null || _goal == ClickGoalKind.None)
        {
            return command;
        }

        if (_target is { IsAlive: false })
        {
            Cancel();
            return command;
        }

        return _goal switch
        {
            ClickGoalKind.Attack => SteerAttack(command, world),
            ClickGoalKind.Build => SteerBuild(command, world),
            _ => SteerWalk(command, world)
        };
    }

    private PlayerCommandFrame SteerWalk(PlayerCommandFrame command, INavWorld world)
    {
        if (!_pathing.HasPath)
        {
            Cancel();
            return command;
        }

        // A send stall zeroed the axes: leave the command alone and do not step (stuck detection
        // pauses with it).
        return command.MoveIntent.ForwardAxis == 0f ? command : Walk(command, world);
    }

    private PlayerCommandFrame SteerAttack(PlayerCommandFrame command, INavWorld world)
    {
        IClickTarget target = _target!;
        PlayerInteractionMode? requestedMode = null;
        if (_host.Mode != PlayerInteractionMode.Combat && !_modeSent)
        {
            _modeSent = true;
            requestedMode = PlayerInteractionMode.Combat;
        }

        if (InAttackRange())
        {
            _pathing.Clear();
            // Primary only once the server mode is Combat, or a build would land instead of a hit.
            command = Aim(command, target.Anchor - _host.Eye, _host.Mode == PlayerInteractionMode.Combat, requestedMode);
            return WithForward(command, 0f);
        }

        // Out of range with no path left (a stop in range, or Arrived at the nearest cell) replans on
        // the throttle whatever the anchor moved; no way to the enemy ends the goal.
        if (_ticksSincePlan >= EnemyRepathTicks && (!_pathing.HasPath || (target.Anchor - _plannedAnchor).Length > EnemyRepathMetres))
        {
            bool planned = Plan(world, NavCell.FromBody(target.Anchor), viaStartCentre: false);
            _plannedAnchor = target.Anchor;
            if (!planned || !_pathing.HasPath)
            {
                Cancel();
                return command;
            }
        }

        if (requestedMode.HasValue)
        {
            command = command with { RequestedMode = new PlayerModeSwitchIntent(requestedMode) };
        }

        return !_pathing.HasPath || command.MoveIntent.ForwardAxis == 0f ? command : Walk(command, world);
    }

    private PlayerCommandFrame SteerBuild(PlayerCommandFrame command, INavWorld world)
    {
        Vector3 centre = _goalCell.Centre;
        float distance = (centre - _host.Eye).Length;
        if (_buildWalking ? distance <= BuildStopMetres : distance <= BuildStartWalkMetres)
        {
            _pathing.Clear();
            _buildWalking = false;
            // Primary for exactly one tick, then the goal is done.
            bool press = !_buildPulsed;
            command = WithForward(Aim(command, centre - _host.Eye, press, mode: null), 0f);
            if (_buildPulsed)
            {
                Cancel();
            }

            _buildPulsed = true;
            return command;
        }

        _buildWalking = true;
        if (!_pathing.HasPath)
        {
            Cancel();
            return command;
        }

        return command.MoveIntent.ForwardAxis == 0f ? command : Walk(command, world);
    }

    // One follower step: face the way of travel (unless the cursor owns facing, then send the axes
    // that move that way), jump when asked, replan when stuck.
    private PlayerCommandFrame Walk(PlayerCommandFrame command, INavWorld world)
    {
        Vector3 body = _host.BodyCentre;
        NavStep step = _pathing.Step(body, world);
        if (step.NeedsReplan)
        {
            NavCell goal = _goal == ClickGoalKind.Attack && _target is not null ? NavCell.FromBody(_target.Anchor) : _goalCell;
            Plan(world, goal, viaStartCentre: true);
            return command;
        }

        if (step.Arrived)
        {
            if (_pathing.GaveUp)
            {
                Cancel();
                return WithForward(command, 0f);
            }

            _pathing.Clear();
            if (_goal == ClickGoalKind.Walk)
            {
                _goal = ClickGoalKind.None;
            }

            return WithForward(command, 0f);
        }

        Vector3 direction = new(step.Direction.X, 0f, step.Direction.Z);
        bool moving = direction.LengthSquared > 0.000001f;
        float forward = 0f;
        float strafe = 0f;
        if (moving)
        {
            direction.Normalize();
            if (_host.AimOwnsFacing)
            {
                (forward, strafe) = PlayerController.ProjectWorld(direction, _host.Facing);
            }
            else
            {
                _host.SetFacing(direction);
                forward = 1f;
            }
        }

        // No direction means the follower is holding still for a hop.
        PlayerMoveIntent move = command.MoveIntent;
        return command with
        {
            MoveIntent = move with { ForwardAxis = forward, StrafeAxis = strafe, JumpPressed = move.JumpPressed || step.Jump }
        };
    }

    private PlayerCommandFrame Aim(PlayerCommandFrame command, Vector3 direction, bool primary, PlayerInteractionMode? mode)
    {
        if (direction.LengthSquared > 0.000001f)
        {
            _host.SetFacing(direction.Normalized());
        }

        return command with
        {
            RequestedMode = mode.HasValue ? new PlayerModeSwitchIntent(mode) : command.RequestedMode,
            PrimaryUsePressed = primary
        };
    }

    private static PlayerCommandFrame WithForward(PlayerCommandFrame command, float forward) =>
        command with { MoveIntent = command.MoveIntent with { ForwardAxis = forward, StrafeAxis = 0f } };

    // Combat: walk to the ground point, or approach and attack an enemy. Building: walk up to the
    // cell and place one block; an enemy is still attacked.
    private void OnRightPress(Vector2 position)
    {
        if (!_host.TryPick(position, out ClickPick pick) || _host.World is not { } world)
        {
            return;
        }

        _ticksSinceHeldPick = 0;
        // An enemy is attacked in either mode (a press in Building asks for Combat first); the ground
        // is walked to in Combat and built on in Building.
        if (_host.Mode == PlayerInteractionMode.Combat || pick.Kind == ClickPickKind.Enemy)
        {
            OnCombatPress(pick, world);
            return;
        }

        if (pick.Kind != ClickPickKind.Ground)
        {
            return;
        }

        BeginGoal(ClickGoalKind.Build, pick.Cell);
        _buildPulsed = false;
        if ((pick.Cell.Centre - _host.Eye).Length > BuildStartWalkMetres)
        {
            _buildWalking = true;
            if (!Plan(world, pick.Cell, viaStartCentre: false))
            {
                Cancel();
            }
        }
    }

    private void OnCombatPress(ClickPick pick, INavWorld world)
    {
        if (pick.Kind == ClickPickKind.Enemy && pick.Target is { IsAlive: true } enemy)
        {
            BeginGoal(ClickGoalKind.Attack, NavCell.FromBody(enemy.Anchor));
            _target = enemy;
            _plannedAnchor = enemy.Anchor;
            Plan(world, _goalCell, viaStartCentre: false);
        }
        else if (pick.Kind == ClickPickKind.Ground)
        {
            BeginGoal(ClickGoalKind.Walk, pick.Cell);
            if (!Plan(world, pick.Cell, viaStartCentre: false))
            {
                Cancel();
            }
        }
    }

    private void RepathToCursor(Vector2 position)
    {
        _ticksSinceHeldPick = 0;
        if (!_host.TryPick(position, out ClickPick pick) || pick.Kind != ClickPickKind.Ground || (_goal == ClickGoalKind.Walk && pick.Cell == _goalCell) || _host.World is not { } world)
        {
            return;
        }

        if (_goal == ClickGoalKind.None)
        {
            BeginGoal(ClickGoalKind.Walk, pick.Cell);
            if (!Plan(world, pick.Cell, viaStartCentre: false))
            {
                Cancel();
            }

            return;
        }

        _goalCell = pick.Cell;
        Plan(world, pick.Cell, viaStartCentre: false);
    }

    private void BeginGoal(ClickGoalKind kind, NavCell cell)
    {
        Cancel();
        _goal = kind;
        _goalCell = cell;
        _modeSent = false;
        _ticksSincePlan = 0;
    }

    private bool Plan(INavWorld world, NavCell goal, bool viaStartCentre)
    {
        _ticksSincePlan = 0;
        return _pathing.Plan(world, NavCell.FromBody(_host.BodyCentre), goal, viaStartCentre, out _);
    }

    // The weapon's reach in metres (WeaponReach of the equipped main hand), set when the inventory changes.
    // The swing starts at a fraction of it, so the server's reach check still passes after the last step.
    public float AttackReachMetres { get; set; } = WeaponReach.UnarmedMetres;

    // Horizontal body centre to anchor, the measure the server's reach gate uses, plus the server's
    // wall test from the eye to the anchor.
    private bool InAttackRange()
    {
        if (_goal != ClickGoalKind.Attack || _target is not { IsAlive: true } t)
        {
            return false;
        }

        float dx = t.Anchor.X - _host.BodyCentre.X;
        float dz = t.Anchor.Z - _host.BodyCentre.Z;
        float reach = AttackReachMetres * WeaponReach.ClientApproachFraction;
        if (dx * dx + dz * dz > reach * reach)
        {
            return false;
        }

        // Reach alone is not enough for a long weapon: it would stop behind a wall and swing into it.
        return _host.World is not ISightWorld sight || sight.HasClearLine(_host.Eye, t.Anchor);
    }

    private bool InBuildRange() =>
        _goal == ClickGoalKind.Build && (_goalCell.Centre - _host.Eye).Length <= (_buildWalking ? BuildStopMetres : BuildStartWalkMetres);
}
