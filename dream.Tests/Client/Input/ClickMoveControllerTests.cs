using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;

public class ClickMoveControllerTests
{
    private sealed class FakeNav : INavWorld
    {
        public int Width => 64;

        public int Height => 16;

        public int Depth => 64;

        public bool IsBlocked(int x, int y, int z) => y == 0;

        public bool IsSupport(int x, int y, int z) => y == 0;
    }

    private sealed class WalledNav : INavWorld, ISightWorld
    {
        public bool Walled { get; set; }

        public int Width => 64;

        public int Height => 16;

        public int Depth => 64;

        public bool IsBlocked(int x, int y, int z) => y == 0;

        public bool IsSupport(int x, int y, int z) => y == 0;

        public bool HasClearLine(Vector3 origin, Vector3 anchor) => !Walled;
    }

    private sealed class FakeTarget : IClickTarget
    {
        public bool IsAlive { get; set; } = true;

        public Vector3 Anchor { get; set; }
    }

    private sealed class FakeHost : IClickMoveHost
    {
        public INavWorld? World { get; set; } = new FakeNav();

        public Vector3 BodyCentre { get; set; } = new(5.5f, 1.5f, 5.5f);

        public Vector3 Eye { get; set; } = new(5.5f, 2.0f, 5.5f);

        public bool NoclipActive { get; set; }

        public PlayerInteractionMode Mode { get; set; } = PlayerInteractionMode.Combat;

        public ClickPick? NextPick { get; set; }

        public int PickCalls { get; private set; }

        public Vector3? LastFacing { get; private set; }

        public bool TryPick(Vector2 screenPosition, out ClickPick pick)
        {
            PickCalls++;
            pick = NextPick ?? default;
            return NextPick.HasValue;
        }

        public Vector3 Facing { get; set; } = Vector3.UnitX;

        public bool AimOwnsFacing { get; set; }

        public void SetFacing(Vector3 direction) => LastFacing = direction;
    }

    private sealed class FakePathing : IClickPathing
    {
        public bool HasPath { get; set; }

        public bool GaveUp { get; private set; }

        public int LastPathCells => 3;

        public int PlanCalls { get; private set; }

        public int StepCalls { get; private set; }

        public NavCell LastGoal { get; private set; }

        public bool LastViaStart { get; private set; }

        public NavStep NextStep { get; set; } = new(Vector3.UnitX, false, false, false);

        // Behaves as PathFollower does when the body cannot move: each Step is one stuck verdict.
        public bool StuckForever { get; set; }

        private int _failedReplans;

        public bool Plan(INavWorld world, NavCell start, NavCell goal, bool viaStartCentre, out bool reachedGoal)
        {
            PlanCalls++;
            LastGoal = goal;
            LastViaStart = viaStartCentre;
            HasPath = true;
            reachedGoal = true;
            if (!viaStartCentre)
            {
                _failedReplans = 0;
                GaveUp = false;
            }

            return true;
        }

        public void Clear()
        {
            HasPath = false;
            _failedReplans = 0;
            GaveUp = false;
        }

        public NavStep Step(Vector3 bodyCentre, INavWorld world)
        {
            StepCalls++;
            if (!StuckForever)
            {
                return NextStep;
            }

            if (_failedReplans >= PathFollower.MaxFailedReplans)
            {
                GaveUp = true;
                HasPath = false;
                return new NavStep(Vector3.Zero, false, true, false);
            }

            _failedReplans++;
            return new NavStep(Vector3.Zero, false, false, true);
        }
    }

    private static readonly Vector2 Spot = new(400f, 300f);

    private static ClickPick Ground(int x, int z) =>
        new(ClickPickKind.Ground, new NavCell(x, 1, z), new Vector3(x + 0.5f, 1f, z + 0.5f), null);

    private static ClickPick Enemy(FakeTarget target) =>
        new(ClickPickKind.Enemy, default, target.Anchor, target);

    private static WorldPointerState Pointer(bool left = false, bool right = false, bool overHud = false) =>
        new(Spot, left, right, overHud);

    private static PlayerCommandFrame Frame(float forward = 0f) =>
        new(
            PlayerSessionId.LocalDefault,
            1,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(forward, 0f),
            PlayerLookIntent.None,
            false);

    private static (ClickMoveController Controller, FakeHost Host, FakePathing Pathing) Create()
    {
        FakeHost host = new();
        FakePathing pathing = new();
        return (new ClickMoveController(host, pathing), host, pathing);
    }

    [Fact]
    public void GroundClick_SetsAWalkGoalAndMarksTheFrameWithAxisOne()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);

        controller.OnFrame(Pointer(right: true), allowGameplayInput: true);

        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
        Assert.Equal(new NavCell(12, 1, 9), pathing.LastGoal);
        Assert.False(pathing.LastViaStart);
        Assert.Equal(1f, controller.MarkFrame(Frame()).MoveIntent.ForwardAxis);
    }

    [Fact]
    public void PressOnTheHud_SetsNoGoal()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);

        controller.OnFrame(Pointer(right: true, overHud: true), allowGameplayInput: true);

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.Equal(0, pathing.PlanCalls);
        Assert.Equal(0, host.PickCalls);
    }

    [Fact]
    public void PressThatStartsOnTheHud_ReleasedOnTheWorld_SetsNoGoal()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);

        controller.OnFrame(Pointer(right: true, overHud: true), true);
        controller.OnFrame(Pointer(right: true, overHud: false), true);
        controller.OnFrame(Pointer(right: false, overHud: false), true);

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.Equal(0, pathing.PlanCalls);
    }

    [Fact]
    public void NoCamera_NoPick_NoClick()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = null;

        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.Equal(0, pathing.PlanCalls);
    }

    [Fact]
    public void SteerTick_LeavesAStallZeroedWalkAlone()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);

        PlayerCommandFrame stalled = Frame(forward: 0f);
        PlayerCommandFrame result = controller.SteerTick(stalled);

        Assert.Equal(stalled, result);
        Assert.Equal(0, pathing.StepCalls);
        Assert.Null(host.LastFacing);
        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
    }

    [Fact]
    public void SteerTick_Walking_FacesTheDirectionOfTravelAndPassesTheJump()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        pathing.NextStep = new NavStep(new Vector3(0f, 0.5f, 2f), Jump: true, Arrived: false, NeedsReplan: false);

        PlayerCommandFrame result = controller.SteerTick(controller.MarkFrame(Frame()));

        Assert.Equal(1f, result.MoveIntent.ForwardAxis);
        Assert.Equal(0f, result.MoveIntent.StrafeAxis);
        Assert.True(result.MoveIntent.JumpPressed);
        Assert.Equal(new Vector3(0f, 0f, 1f), host.LastFacing);
    }

    [Fact]
    public void SteerTick_Arrival_ClearsTheWalkGoalAndStopsTheAxis()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        pathing.NextStep = new NavStep(Vector3.Zero, false, Arrived: true, false);

        PlayerCommandFrame result = controller.SteerTick(controller.MarkFrame(Frame()));

        Assert.Equal(0f, result.MoveIntent.ForwardAxis);
        Assert.Equal(ClickGoalKind.None, controller.Goal);
    }

    [Fact]
    public void AttackHoldsPrimaryOnlyInRange()
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        FakeTarget enemy = new() { Anchor = new Vector3(15.5f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(ClickGoalKind.Attack, controller.Goal);

        PlayerCommandFrame far = controller.SteerTick(controller.MarkFrame(Frame()));
        Assert.False(far.PrimaryUsePressed);
        Assert.Equal(1f, far.MoveIntent.ForwardAxis);

        enemy.Anchor = new Vector3(6.5f, 2f, 5.5f);
        PlayerCommandFrame near = controller.SteerTick(Frame());
        Assert.True(near.PrimaryUsePressed);
        Assert.Equal(0f, near.MoveIntent.ForwardAxis);
        Assert.Equal(Vector3.UnitX, host.LastFacing);
    }

    [Fact]
    public void Attack_StopsAtTheApproachFractionOfTheReach_NotBefore()
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        float stop = controller.AttackReachMetres * WeaponReach.ClientApproachFraction;
        FakeTarget enemy = new() { Anchor = new Vector3(5.5f + stop + 0.05f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);

        Assert.False(controller.SteerTick(controller.MarkFrame(Frame())).PrimaryUsePressed);

        enemy.Anchor = new Vector3(5.5f + stop - 0.05f, 2f, 5.5f);
        Assert.True(controller.SteerTick(Frame()).PrimaryUsePressed);
    }

    [Fact]
    public void Attack_WithALongerWeapon_SwingsFromFurtherAway()
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        FakeTarget enemy = new() { Anchor = new Vector3(5.5f + 8f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);
        Assert.False(controller.SteerTick(controller.MarkFrame(Frame())).PrimaryUsePressed);

        controller.AttackReachMetres = WeaponReach.ForItemKind("gear.long-bow");
        Assert.True(controller.SteerTick(Frame()).PrimaryUsePressed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Attack_InReachBehindAWall_KeepsWalking_AndSwingsOnceTheLineIsClear(bool walled, bool swings)
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        WalledNav nav = new() { Walled = walled };
        host.World = nav;
        controller.AttackReachMetres = WeaponReach.ForItemKind("gear.long-bow");
        FakeTarget enemy = new() { Anchor = new Vector3(5.5f + 8f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);

        PlayerCommandFrame frame = controller.SteerTick(controller.MarkFrame(Frame()));

        Assert.Equal(swings, frame.PrimaryUsePressed);
        Assert.Equal(swings ? 0f : 1f, frame.MoveIntent.ForwardAxis);

        nav.Walled = !walled;
        Assert.Equal(walled, controller.SteerTick(Frame()).PrimaryUsePressed);
    }

    [Fact]
    public void Attack_ReachStartsUnarmed()
    {
        Assert.Equal(WeaponReach.UnarmedMetres, Create().Controller.AttackReachMetres);
    }

    [Fact]
    public void AttackRequestsCombatModeOnce_AndHoldsNoPrimaryUntilItIsCombat()
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Building;
        FakeTarget enemy = new() { Anchor = new Vector3(6.5f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);

        PlayerCommandFrame first = controller.SteerTick(Frame());
        PlayerCommandFrame second = controller.SteerTick(Frame());
        host.Mode = PlayerInteractionMode.Combat;
        PlayerCommandFrame third = controller.SteerTick(Frame());

        Assert.Equal(PlayerInteractionMode.Combat, first.RequestedMode.Mode);
        Assert.False(first.PrimaryUsePressed);
        Assert.Null(second.RequestedMode.Mode);
        Assert.Null(third.RequestedMode.Mode);
        Assert.True(third.PrimaryUsePressed);
    }

    [Fact]
    public void DeadEnemy_ClearsTheAttackGoal()
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        FakeTarget enemy = new() { Anchor = new Vector3(6.5f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);

        enemy.IsAlive = false;
        PlayerCommandFrame result = controller.SteerTick(Frame());

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.False(result.PrimaryUsePressed);
    }

    [Fact]
    public void Build_InRange_PulsesPrimaryForExactlyOneTick()
    {
        var (controller, host, pathing) = Create();
        host.Mode = PlayerInteractionMode.Building;
        host.NextPick = Ground(6, 5);

        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(ClickGoalKind.Build, controller.Goal);
        Assert.Equal(0, pathing.PlanCalls);

        PlayerCommandFrame first = controller.SteerTick(Frame());
        PlayerCommandFrame second = controller.SteerTick(Frame());
        PlayerCommandFrame third = controller.SteerTick(Frame());

        Assert.True(first.PrimaryUsePressed);
        Assert.False(second.PrimaryUsePressed);
        Assert.False(third.PrimaryUsePressed);
        Assert.Equal(ClickGoalKind.None, controller.Goal);
    }

    [Fact]
    public void Build_OutOfRange_WalksUntilWithinTheStopDistanceThenPlaces()
    {
        var (controller, host, pathing) = Create();
        host.Mode = PlayerInteractionMode.Building;
        host.NextPick = Ground(12, 5);

        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(1, pathing.PlanCalls);
        PlayerCommandFrame walking = controller.SteerTick(controller.MarkFrame(Frame()));
        Assert.Equal(1f, walking.MoveIntent.ForwardAxis);
        Assert.False(walking.PrimaryUsePressed);

        host.Eye = new Vector3(10.5f, 2f, 5.5f);
        PlayerCommandFrame arrived = controller.SteerTick(Frame());
        Assert.True(arrived.PrimaryUsePressed);
        Assert.Equal(0f, arrived.MoveIntent.ForwardAxis);
    }

    [Fact]
    public void RightClickInCombat_WalksAndNeverHoldsPrimary()
    {
        var (controller, host, pathing) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        host.NextPick = Ground(8, 5);

        controller.OnFrame(Pointer(right: true), true);
        PlayerCommandFrame held = controller.SteerTick(controller.MarkFrame(Frame()));
        controller.OnFrame(Pointer(right: true), true);
        PlayerCommandFrame stillHeld = controller.SteerTick(controller.MarkFrame(Frame()));

        Assert.False(held.PrimaryUsePressed);
        Assert.False(stillHeld.PrimaryUsePressed);
        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
        Assert.Equal(1, pathing.PlanCalls);
    }

    [Fact]
    public void LeftButton_OpensNoGoalAtAll()
    {
        var (controller, host, pathing) = Create();
        FakeTarget enemy = new() { Anchor = new Vector3(7.5f, 2f, 5.5f) };
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(left: true), true);
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(left: false), true);
        controller.OnFrame(Pointer(left: true), true);

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.Equal(0, pathing.PlanCalls);
        Assert.Equal(0, host.PickCalls);
    }

    [Fact]
    public void HeldRightButton_InBuildingMode_DoesNotWalkToTheCursor()
    {
        var (controller, host, pathing) = Create();
        host.Mode = PlayerInteractionMode.Building;
        host.NextPick = Ground(30, 5);
        controller.OnFrame(Pointer(right: true), true);
        controller.SteerTick(Frame());
        for (int tick = 0; tick < ClickMoveController.HeldRepathTicks; tick++)
        {
            controller.SteerTick(Frame());
        }

        host.NextPick = Ground(40, 5);
        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(new NavCell(30, 1, 5), controller.GoalCell);
        Assert.Equal(1, pathing.PlanCalls);
    }

    [Fact]
    public void SteerTick_WalkingWhileTheCursorOwnsFacing_KeepsTheFacingAndProjectsTheTravelDirection()
    {
        var (controller, host, pathing) = Create();
        host.AimOwnsFacing = true;
        host.Facing = Vector3.UnitX;
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        // Travel along +Z while facing +X: Cross(+X, Up) is +Z, so this is a pure strafe.
        pathing.NextStep = new NavStep(new Vector3(0f, 0f, 3f), false, false, false);

        PlayerCommandFrame result = controller.SteerTick(controller.MarkFrame(Frame()));

        Assert.Null(host.LastFacing);
        Assert.Equal(0f, result.MoveIntent.ForwardAxis, 4);
        Assert.Equal(1f, result.MoveIntent.StrafeAxis, 4);
    }

    [Fact]
    public void HeldRightButton_RepathsTowardTheCursorAtMostEverySixteenTicks()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(1, pathing.PlanCalls);

        host.NextPick = Ground(20, 9);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(1, pathing.PlanCalls);

        for (int tick = 0; tick < ClickMoveController.HeldRepathTicks; tick++)
        {
            controller.SteerTick(controller.MarkFrame(Frame()));
        }

        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(2, pathing.PlanCalls);
        Assert.Equal(new NavCell(20, 1, 9), pathing.LastGoal);
    }

    [Fact]
    public void LosingGameplayInput_CancelsTheGoal()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);

        controller.OnFrame(Pointer(right: false), allowGameplayInput: false);

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.False(pathing.HasPath);
        Assert.Equal(0f, controller.MarkFrame(Frame()).MoveIntent.ForwardAxis);
    }

    [Fact]
    public void ClicksAreIgnoredWhileNoclipIsOn()
    {
        var (controller, host, pathing) = Create();
        host.NoclipActive = true;
        host.NextPick = Ground(12, 9);

        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.Equal(0, pathing.PlanCalls);
    }

    [Fact]
    public void StuckReplans_GiveUpAfterThree()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        pathing.StuckForever = true;

        for (int n = 1; n <= PathFollower.MaxFailedReplans; n++)
        {
            controller.SteerTick(controller.MarkFrame(Frame()));
            Assert.Equal(ClickGoalKind.Walk, controller.Goal);
            Assert.True(pathing.LastViaStart);
        }

        PlayerCommandFrame result = controller.SteerTick(controller.MarkFrame(Frame()));

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.Equal(0f, result.MoveIntent.ForwardAxis);
        Assert.Equal(1 + PathFollower.MaxFailedReplans, pathing.PlanCalls);
    }

    [Fact]
    public void RealFollower_ABodyThatCannotMove_EndsTheGoal()
    {
        FakeHost host = new() { NextPick = Ground(12, 5) };
        ClickMoveController controller = new(host, new NavPathing());
        controller.OnFrame(Pointer(right: true), true);

        int ticks = 0;
        while (controller.Goal != ClickGoalKind.None && ticks < PathFollower.StuckWindowTicks * 20)
        {
            controller.SteerTick(controller.MarkFrame(Frame()));
            ticks++;
        }

        // A jump window then a replan window for each of the three replans, then the same to give up.
        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.Equal(PathFollower.StuckWindowTicks * 2 * (PathFollower.MaxFailedReplans + 1), ticks);
        Assert.False(controller.IsWalking);
    }

    [Fact]
    public void NewClick_AfterArrival_StartsANewWalk()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        pathing.NextStep = new NavStep(Vector3.Zero, false, true, false);
        controller.SteerTick(controller.MarkFrame(Frame()));
        Assert.Equal(ClickGoalKind.None, controller.Goal);
        controller.OnFrame(Pointer(right: false), true);

        host.NextPick = Ground(4, 4);
        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
        Assert.Equal(new NavCell(4, 1, 4), pathing.LastGoal);
        Assert.Equal(1f, controller.MarkFrame(Frame()).MoveIntent.ForwardAxis);
    }

    [Fact]
    public void NewClick_AfterGivingUp_StartsANewWalk()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        pathing.StuckForever = true;
        for (int n = 0; n <= PathFollower.MaxFailedReplans; n++)
        {
            controller.SteerTick(controller.MarkFrame(Frame()));
        }

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        controller.OnFrame(Pointer(right: false), true);

        pathing.StuckForever = false;
        host.NextPick = Ground(3, 3);
        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
        Assert.Equal(new NavCell(3, 1, 3), pathing.LastGoal);
        Assert.False(pathing.LastViaStart);
        PlayerCommandFrame walking = controller.SteerTick(controller.MarkFrame(Frame()));
        Assert.Equal(1f, walking.MoveIntent.ForwardAxis);
    }

    [Fact]
    public void NewClick_AfterAnAttackStoppedShortAtTheNearestSpot_ReplacesTheAttack()
    {
        var (controller, host, pathing) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        FakeTarget enemy = new() { Anchor = new Vector3(30.5f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);
        // The path ends at the nearest reachable spot, still out of range of the enemy.
        pathing.NextStep = new NavStep(Vector3.Zero, false, true, false);
        controller.SteerTick(controller.MarkFrame(Frame()));
        Assert.Equal(ClickGoalKind.Attack, controller.Goal);
        Assert.False(controller.IsWalking);
        controller.OnFrame(Pointer(right: false), true);

        host.NextPick = Ground(8, 5);
        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
        Assert.Null(controller.Target);
        Assert.Equal(new NavCell(8, 1, 5), pathing.LastGoal);
    }

    [Fact]
    public void NewClick_AfterTheTargetDied_StartsANewAttack()
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        FakeTarget first = new() { Anchor = new Vector3(6.5f, 2f, 5.5f) };
        host.NextPick = Enemy(first);
        controller.OnFrame(Pointer(right: true), true);
        first.IsAlive = false;
        controller.SteerTick(Frame());
        Assert.Equal(ClickGoalKind.None, controller.Goal);
        controller.OnFrame(Pointer(right: false), true);

        FakeTarget second = new() { Anchor = new Vector3(6.5f, 2f, 6.0f) };
        host.NextPick = Enemy(second);
        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(ClickGoalKind.Attack, controller.Goal);
        Assert.Same(second, controller.Target);
        Assert.True(controller.SteerTick(Frame()).PrimaryUsePressed);
    }

    [Fact]
    public void NewClick_AfterDeathAndRespawn_Works()
    {
        var (controller, host, pathing) = Create();
        host.Mode = PlayerInteractionMode.Combat;
        FakeTarget enemy = new() { Anchor = new Vector3(30.5f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);

        // Dead: gameplay input is off for the whole death, whatever the buttons do.
        controller.OnFrame(Pointer(right: false), allowGameplayInput: false);
        controller.OnFrame(Pointer(right: true), allowGameplayInput: false);
        Assert.Equal(ClickGoalKind.None, controller.Goal);
        controller.OnFrame(Pointer(right: false), allowGameplayInput: false);

        // Respawned: the next press is a fresh click.
        host.Mode = PlayerInteractionMode.Combat;
        host.NextPick = Ground(6, 7);
        controller.OnFrame(Pointer(right: true), true);

        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
        Assert.Equal(new NavCell(6, 1, 7), pathing.LastGoal);
    }

    [Fact]
    public void ButtonStillHeldWhenInputReturns_IsNotAClickUntilReleasedAndPressedAgain()
    {
        var (controller, host, _) = Create();
        host.NextPick = Ground(6, 7);
        controller.OnFrame(Pointer(right: true), allowGameplayInput: false);

        controller.OnFrame(Pointer(right: true), allowGameplayInput: true);
        Assert.Equal(ClickGoalKind.None, controller.Goal);

        controller.OnFrame(Pointer(right: false), true);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
    }

    [Fact]
    public void TurningNoclipOn_CancelsAGoalUnderWay_AndClicksWorkAgainAfterwards()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(12, 9);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(ClickGoalKind.Walk, controller.Goal);

        host.NoclipActive = true;
        controller.OnFrame(Pointer(right: false), true);

        Assert.Equal(ClickGoalKind.None, controller.Goal);
        Assert.False(pathing.HasPath);
        Assert.Equal(0f, controller.MarkFrame(Frame()).MoveIntent.ForwardAxis);
        Assert.Equal(0f, controller.SteerTick(Frame()).MoveIntent.ForwardAxis);

        host.NoclipActive = false;
        host.NextPick = Ground(2, 2);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
    }

    [Fact]
    public void RightClickInBuildingMode_AtDistance_WalksThenPlacesOnceAndAnotherRightClickWorks()
    {
        var (controller, host, pathing) = Create();
        host.Mode = PlayerInteractionMode.Building;
        host.NextPick = Ground(14, 5);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(ClickGoalKind.Build, controller.Goal);
        Assert.True(controller.IsWalking);

        host.Eye = new Vector3(12.5f, 2f, 5.5f);
        int placed = 0;
        for (int tick = 0; tick < 4; tick++)
        {
            placed += controller.SteerTick(Frame()).PrimaryUsePressed ? 1 : 0;
        }

        Assert.Equal(1, placed);
        Assert.Equal(ClickGoalKind.None, controller.Goal);
        controller.OnFrame(Pointer(right: false), true);

        host.NextPick = Ground(13, 7);
        controller.OnFrame(Pointer(right: true), true);
        Assert.Equal(ClickGoalKind.Build, controller.Goal);
        Assert.Equal(new NavCell(13, 1, 7), controller.GoalCell);
        Assert.Equal(1, pathing.PlanCalls);
    }

    [Fact]
    public void RightClickOnAnEnemy_InBuildingMode_AsksForCombatAndAttacksOnceInRange()
    {
        var (controller, host, _) = Create();
        host.Mode = PlayerInteractionMode.Building;
        FakeTarget enemy = new() { Anchor = new Vector3(6.5f, 2f, 5.5f) };
        host.NextPick = Enemy(enemy);
        controller.OnFrame(Pointer(right: true), true);

        PlayerCommandFrame first = controller.SteerTick(Frame());
        Assert.Equal(PlayerInteractionMode.Combat, first.RequestedMode.Mode);
        Assert.False(first.PrimaryUsePressed);

        host.Mode = PlayerInteractionMode.Combat;
        PlayerCommandFrame second = controller.SteerTick(Frame());
        Assert.True(second.PrimaryUsePressed);
        Assert.Equal(ClickGoalKind.Attack, controller.Goal);
    }

    [Fact]
    public void HeldRightButton_FollowsTheCursorAcrossSeveralCells()
    {
        var (controller, host, pathing) = Create();
        host.NextPick = Ground(10, 5);
        controller.OnFrame(Pointer(right: true), true);

        for (int cell = 11; cell <= 13; cell++)
        {
            for (int tick = 0; tick < ClickMoveController.HeldRepathTicks; tick++)
            {
                controller.SteerTick(controller.MarkFrame(Frame()));
            }

            host.NextPick = Ground(cell, 5);
            controller.OnFrame(Pointer(right: true), true);
            Assert.Equal(new NavCell(cell, 1, 5), pathing.LastGoal);
            Assert.Equal(new NavCell(cell, 1, 5), controller.GoalCell);
        }

        Assert.Equal(4, pathing.PlanCalls);
        controller.OnFrame(Pointer(right: false), true);
        Assert.Equal(ClickGoalKind.Walk, controller.Goal);
    }
}
