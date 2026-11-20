using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Rendering.Core;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;

public class EnemyHoverProbeTests
{
    private sealed class Target : IClickTarget
    {
        public bool IsAlive { get; set; } = true;

        public Vector3 Anchor { get; set; }
    }

    private sealed class Host : IClickMoveHost
    {
        public ClickPick? NextPick { get; set; }

        public INavWorld? World => null;

        public Vector3 BodyCentre => Vector3.Zero;

        public Vector3 Eye => Vector3.Zero;

        public bool NoclipActive => false;

        public PlayerInteractionMode Mode => PlayerInteractionMode.Building;

        public Vector3 Facing => Vector3.UnitX;

        public bool AimOwnsFacing => false;

        public bool TryPick(Vector2 screenPosition, out ClickPick pick)
        {
            pick = NextPick ?? default;
            return NextPick.HasValue;
        }

        public void SetFacing(Vector3 direction) { }
    }

    private static ClickPick EnemyPick(bool alive = true) =>
        new(ClickPickKind.Enemy, default, Vector3.Zero, new Target { IsAlive = alive });

    private static WorldPointerState Pointer(bool overHud = false, float x = 10f) =>
        new(new Vector2(x, 20f), false, false, overHud);

    [Fact]
    public void LiveEnemyUnderTheCursor_IsHovered_EvenInBuildingMode()
    {
        Host host = new() { NextPick = EnemyPick() };
        Assert.True(new EnemyHoverProbe(host).Evaluate(Pointer(), true, true));
    }

    [Fact]
    public void OverTheHud_IsNotHovered()
    {
        Host host = new() { NextPick = EnemyPick() };
        Assert.False(new EnemyHoverProbe(host).Evaluate(Pointer(overHud: true), true, true));
    }

    [Fact]
    public void GameplayInputNotAllowed_IsNotHovered()
    {
        Host host = new() { NextPick = EnemyPick() };
        Assert.False(new EnemyHoverProbe(host).Evaluate(Pointer(), false, true));
    }

    [Fact]
    public void UnfocusedWindow_IsNotHovered()
    {
        Host host = new() { NextPick = EnemyPick() };
        Assert.False(new EnemyHoverProbe(host).Evaluate(Pointer(), true, false));
    }

    [Fact]
    public void GroundPick_IsNotHovered()
    {
        Host host = new() { NextPick = new ClickPick(ClickPickKind.Ground, default, Vector3.Zero, null) };
        Assert.False(new EnemyHoverProbe(host).Evaluate(Pointer(), true, true));
    }

    [Fact]
    public void NoPick_IsNotHovered()
    {
        Assert.False(new EnemyHoverProbe(new Host()).Evaluate(Pointer(), true, true));
    }

    [Fact]
    public void DeadTarget_IsNotHovered()
    {
        Host host = new() { NextPick = EnemyPick(alive: false) };
        Assert.False(new EnemyHoverProbe(host).Evaluate(Pointer(), true, true));
    }

    [Fact]
    public void NonFinitePosition_IsNotHovered()
    {
        Host host = new() { NextPick = EnemyPick() };
        EnemyHoverProbe probe = new(host);
        Assert.False(probe.Evaluate(Pointer(x: float.NaN), true, true));
        Assert.False(probe.Evaluate(Pointer(x: float.PositiveInfinity), true, true));
    }
}
