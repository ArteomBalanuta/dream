using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Rendering.Core;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-INPUT-4: the click that gives the window the focus back must not act as a gameplay press.
//
// Render.SampleWorldPointer forces LeftDown/RightDown false while the window is unfocused, so both
// controllers latch their "previous" button state to false. On the first focused frame the physical
// button is still down (the focused value is written in Render and read in the next Update, so it lags
// a frame), and without a guard both read a press EDGE: the weapon swings, or a click-to-move / attack
// goal is issued, at the point the player clicked only to come back to the game.
//
// The guard is the one UiInputRouter.BeginFrame already uses when the UI becomes interactive again:
// seed the previous state from the pointer on the first focused frame and skip that single edge. The
// control tests below are the important half — a window that already had the focus must still act on
// its very first click, which would otherwise be a constant annoyance instead of an occasional one.
public class InputRefocusClickTests
{
    private static WorldPointerState Pointer(bool left = false, bool right = false, bool overHud = false) =>
        new(new Vector2(14f, 10f), left, right, overHud);

    // ---- the cursor aim (left button: the held attack) ----

    private sealed class AimHost : ICursorAimHost
    {
        public Vector3 BodyCentre => new(10f, 1.5f, 10f);

        public PlayerInteractionMode Mode => PlayerInteractionMode.Combat;

        public bool TryBuildRay(Vector2 screenPosition, out Vector3 origin, out Vector3 direction)
        {
            origin = new Vector3(screenPosition.X, 11.5f, screenPosition.Y);
            direction = -Vector3.UnitY;
            return true;
        }
    }

    // The control: the window had the focus all along, so the first click still attacks.
    [Fact]
    public void CursorAim_AFocusedWindow_StillAttacksOnTheFirstClick()
    {
        CursorAimController aim = new(new AimHost());

        aim.OnFrame(Pointer(), true, false, windowFocused: true);
        aim.OnFrame(Pointer(left: true), true, false, windowFocused: true);

        Assert.True(aim.AttackHeld);
    }

    // BH-INPUT-4: the click that refocuses the window must not arm an attack, and holding it must not
    // start one later either — only a fresh press may.
    [Fact]
    public void CursorAim_TheClickThatRefocusesTheWindow_DoesNotAttack()
    {
        CursorAimController aim = new(new AimHost());

        // Unfocused: the pointer reports no buttons whatever the hand is doing.
        aim.OnFrame(Pointer(), true, false, windowFocused: false);
        // The refocusing click: focused again, and the button is still physically down.
        aim.OnFrame(Pointer(left: true), true, false, windowFocused: true);
        Assert.False(aim.AttackHeld);

        // Still held: it never becomes an attack retroactively.
        aim.OnFrame(Pointer(left: true), true, false, windowFocused: true);
        Assert.False(aim.AttackHeld);

        // Released, then pressed again by hand: that one attacks.
        aim.OnFrame(Pointer(), true, false, windowFocused: true);
        aim.OnFrame(Pointer(left: true), true, false, windowFocused: true);
        Assert.True(aim.AttackHeld);
    }

    // ---- click to move (right button: the goal) ----

    private sealed class FakeNav : INavWorld
    {
        public int Width => 64;

        public int Height => 16;

        public int Depth => 64;

        public bool IsBlocked(int x, int y, int z) => y == 0;

        public bool IsSupport(int x, int y, int z) => y == 0;
    }

    private sealed class MoveHost : IClickMoveHost
    {
        public INavWorld? World { get; set; } = new FakeNav();

        public Vector3 BodyCentre => new(5.5f, 1.5f, 5.5f);

        public Vector3 Eye => new(5.5f, 2.0f, 5.5f);

        public bool NoclipActive => false;

        public PlayerInteractionMode Mode => PlayerInteractionMode.Combat;

        public int PickCalls { get; private set; }

        public bool TryPick(Vector2 screenPosition, out ClickPick pick)
        {
            PickCalls++;
            pick = new ClickPick(ClickPickKind.Ground, new NavCell(9, 1, 9), new Vector3(9.5f, 1f, 9.5f), null);
            return true;
        }

        public Vector3 Facing => Vector3.UnitX;

        public bool AimOwnsFacing => false;

        public void SetFacing(Vector3 direction)
        {
        }
    }

    private sealed class FakePathing : IClickPathing
    {
        public bool HasPath { get; private set; }

        public bool GaveUp => false;

        public int LastPathCells => 3;

        public bool Plan(INavWorld world, NavCell start, NavCell goal, bool viaStartCentre, out bool reachedGoal)
        {
            HasPath = true;
            reachedGoal = true;
            return true;
        }

        public void Clear() => HasPath = false;

        public NavStep Step(Vector3 bodyCentre, INavWorld world) => default;
    }

    // The control: the window had the focus, so the first right click still opens a goal.
    [Fact]
    public void ClickMove_AFocusedWindow_StillTakesTheFirstRightClick()
    {
        MoveHost host = new();
        ClickMoveController move = new(host, new FakePathing());

        move.OnFrame(Pointer(), true, windowFocused: true);
        move.OnFrame(Pointer(right: true), true, windowFocused: true);

        Assert.Equal(1, host.PickCalls);
        Assert.NotEqual(ClickGoalKind.None, move.Goal);
    }

    // BH-INPUT-4: the click that refocuses the window must not pick, and must not set a goal.
    [Fact]
    public void ClickMove_TheClickThatRefocusesTheWindow_SetsNoGoal()
    {
        MoveHost host = new();
        ClickMoveController move = new(host, new FakePathing());

        move.OnFrame(Pointer(), true, windowFocused: false);
        move.OnFrame(Pointer(right: true), true, windowFocused: true);

        Assert.Equal(0, host.PickCalls);
        Assert.Equal(ClickGoalKind.None, move.Goal);

        // Released and pressed again by hand: that one opens a goal.
        move.OnFrame(Pointer(), true, windowFocused: true);
        move.OnFrame(Pointer(right: true), true, windowFocused: true);
        Assert.Equal(1, host.PickCalls);
        Assert.NotEqual(ClickGoalKind.None, move.Goal);
    }
}
