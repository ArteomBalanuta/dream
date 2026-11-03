using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Rendering.Core;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;

public class CursorAimControllerTests
{
    // A straight-down ray at the cursor's own (x, y) read as world (x, z), 10 m up: the ground point
    // is the cursor position itself, so the expectations are plain numbers.
    private sealed class FakeHost : ICursorAimHost
    {
        public Vector3 BodyCentre { get; set; } = new(10f, 1.5f, 10f);

        public PlayerInteractionMode Mode { get; set; } = PlayerInteractionMode.Combat;

        public bool HasCamera { get; set; } = true;

        public Vector3 RayDirection { get; set; } = -Vector3.UnitY;

        public int RayCalls { get; private set; }

        public bool TryBuildRay(Vector2 screenPosition, out Vector3 origin, out Vector3 direction)
        {
            RayCalls++;
            origin = new Vector3(screenPosition.X, 11.5f, screenPosition.Y);
            direction = RayDirection;
            return HasCamera;
        }
    }

    private static WorldPointerState Pointer(float x, float z, bool left = false, bool overHud = false) =>
        new(new Vector2(x, z), left, false, overHud);

    private static (CursorAimController Aim, FakeHost Host) Create()
    {
        FakeHost host = new();
        return (new CursorAimController(host), host);
    }

    [Fact]
    public void Facing_IsTheFlatDirectionToTheCursorsGroundPoint()
    {
        var (aim, _) = Create();

        aim.OnFrame(Pointer(14f, 10f), true, false);

        Assert.True(aim.OwnsFacing);
        Assert.True(aim.TryGetFacing(out Vector3 facing));
        Assert.Equal(1f, facing.X, 4);
        Assert.Equal(0f, facing.Y, 4);
        Assert.Equal(0f, facing.Z, 4);
    }

    [Fact]
    public void Facing_IsYawOnly_WhateverTheHeightOfTheRayOrigin()
    {
        var (aim, host) = Create();
        host.RayDirection = Vector3.Normalize(new Vector3(0.5f, -1f, 0.5f));

        aim.OnFrame(Pointer(10f, 10f), true, false);

        Assert.True(aim.TryGetFacing(out Vector3 facing));
        Assert.Equal(0f, facing.Y);
        Assert.Equal(1f, facing.Length, 4);
        Assert.Equal(facing.X, facing.Z, 4);
    }

    [Fact]
    public void Facing_FollowsTheBodyBetweenFrames_ForTheSameCursor()
    {
        var (aim, host) = Create();
        aim.OnFrame(Pointer(14f, 10f), true, false);

        host.BodyCentre = new Vector3(14f, 1.5f, 6f);

        Assert.True(aim.TryGetFacing(out Vector3 facing));
        Assert.Equal(0f, facing.X, 4);
        Assert.Equal(1f, facing.Z, 4);
    }

    [Fact]
    public void CursorOnTheBody_LeavesTheFacingAlone()
    {
        var (aim, _) = Create();

        aim.OnFrame(Pointer(10.1f, 10.1f), true, false);

        Assert.True(aim.OwnsFacing);
        Assert.False(aim.TryGetFacing(out _));
    }

    [Fact]
    public void NoCamera_OrARayThatNeverMeetsTheGround_OwnsNoFacing()
    {
        var (aim, host) = Create();
        host.HasCamera = false;
        aim.OnFrame(Pointer(14f, 10f), true, false);
        Assert.False(aim.OwnsFacing);
        Assert.False(aim.TryGetFacing(out _));

        host.HasCamera = true;
        host.RayDirection = Vector3.UnitX;
        aim.OnFrame(Pointer(14f, 10f), true, false);
        Assert.False(aim.OwnsFacing);

        host.RayDirection = Vector3.UnitY;
        aim.OnFrame(Pointer(14f, 10f), true, false);
        Assert.False(aim.OwnsFacing);
    }

    [Fact]
    public void HoldingLeft_AttacksOnEveryFrame_AndStopsTheFrameItIsReleased()
    {
        var (aim, _) = Create();
        List<bool> held = [];

        foreach (bool left in new[] { false, true, true, true, true, false, false })
        {
            aim.OnFrame(Pointer(14f, 10f, left), true, false);
            held.Add(aim.AttackHeld);
        }

        Assert.Equal(new[] { false, true, true, true, true, false, false }, held);
    }

    [Fact]
    public void PressStartingOverTheHud_NeverAttacks_EvenDraggedOntoTheWorld()
    {
        var (aim, _) = Create();

        aim.OnFrame(Pointer(14f, 10f, left: true, overHud: true), true, false);
        Assert.False(aim.AttackHeld);
        aim.OnFrame(Pointer(14f, 10f, left: true, overHud: false), true, false);
        Assert.False(aim.AttackHeld);
        aim.OnFrame(Pointer(14f, 10f, left: false), true, false);
        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);
        Assert.True(aim.AttackHeld);
    }

    [Fact]
    public void PressStartingInTheWorld_KeepsAttackingOverTheHud()
    {
        var (aim, _) = Create();

        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);
        aim.OnFrame(Pointer(14f, 10f, left: true, overHud: true), true, false);

        Assert.True(aim.AttackHeld);
    }

    [Fact]
    public void BuildingMode_DoesNotAttack_ButStillAims()
    {
        var (aim, host) = Create();
        host.Mode = PlayerInteractionMode.Building;

        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);

        Assert.False(aim.AttackHeld);
        Assert.True(aim.TryGetFacing(out _));
    }

    [Fact]
    public void SwitchingToCombatWhileHolding_StartsAttacking()
    {
        var (aim, host) = Create();
        host.Mode = PlayerInteractionMode.Building;
        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);
        Assert.False(aim.AttackHeld);

        host.Mode = PlayerInteractionMode.Combat;
        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);

        Assert.True(aim.AttackHeld);
    }

    [Fact]
    public void Suppressed_NeitherAimsNorAttacks()
    {
        var (aim, host) = Create();

        aim.OnFrame(Pointer(14f, 10f, left: true), true, suppressed: true);

        Assert.False(aim.AttackHeld);
        Assert.False(aim.OwnsFacing);
        Assert.False(aim.TryGetFacing(out _));
        Assert.Equal(0, host.RayCalls);
    }

    [Fact]
    public void GameplayInputOff_NeitherAimsNorAttacks_AndAHeldButtonNeedsAFreshPressOnReturn()
    {
        var (aim, _) = Create();
        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);
        Assert.True(aim.AttackHeld);

        aim.OnFrame(Pointer(14f, 10f, left: true), false, false);
        Assert.False(aim.AttackHeld);
        Assert.False(aim.OwnsFacing);

        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);
        Assert.False(aim.AttackHeld);
        Assert.True(aim.OwnsFacing);

        aim.OnFrame(Pointer(14f, 10f, left: false), true, false);
        aim.OnFrame(Pointer(14f, 10f, left: true), true, false);
        Assert.True(aim.AttackHeld);
    }

    [Fact]
    public void OneRayPerFrame_NoPerTickWork()
    {
        var (aim, host) = Create();
        aim.OnFrame(Pointer(14f, 10f), true, false);

        for (int tick = 0; tick < 10; tick++)
        {
            aim.TryGetFacing(out _);
        }

        Assert.Equal(1, host.RayCalls);
    }
}
