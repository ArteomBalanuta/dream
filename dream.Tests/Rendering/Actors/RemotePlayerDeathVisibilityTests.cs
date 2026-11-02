using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Simulation.Players;

namespace Dream.Tests.Rendering.Actors;

public class RemotePlayerDeathVisibilityTests
{
    private static RemotePlayerPresentationState Player(bool alive, float deathAge = float.PositiveInfinity, bool moving = false, float speed = 0f) =>
        new(
            2,
            "remote-1",
            "Remote",
            new InterpolatedActorPose(new Vector3(3f, 1.5f, 3f), Vector3.UnitZ),
            alive ? 100 : 0,
            100,
            alive,
            IsMoving: moving,
            HorizontalSpeed: speed,
            DeathAgeSeconds: deathAge);

    private static Camera CameraAtOrigin() => new(Vector3.Zero);

    [Fact]
    public void ADyingPlayerStaysDrawn_UntilTheDeathAnimationHasPlayed()
    {
        Camera camera = CameraAtOrigin();

        Assert.Single(RemotePlayerRenderer.SelectVisiblePlayers(camera, [Player(alive: false, deathAge: 0.5f)]));
        Assert.Single(RemotePlayerRenderer.SelectVisiblePlayers(
            camera, [Player(alive: false, deathAge: RemotePlayerRenderer.DeathLingerSeconds - 0.1f)]));
        Assert.Empty(RemotePlayerRenderer.SelectVisiblePlayers(
            camera, [Player(alive: false, deathAge: RemotePlayerRenderer.DeathLingerSeconds + 0.1f)]));
    }

    [Fact]
    public void ADeadPlayerOfUnknownAge_IsHidden()
    {
        Assert.Empty(RemotePlayerRenderer.SelectVisiblePlayers(CameraAtOrigin(), [Player(alive: false)]));
    }

    [Fact]
    public void AliveCountStillExcludesTheDying()
    {
        Assert.Equal(1, RemotePlayerRenderer.CountAlive([Player(alive: false, deathAge: 0.5f), Player(alive: true)]));
    }

    [Fact]
    public void ResolveSpeed_UsesTheMeasuredSpeed_OrTheAuthoredPaceWhenMovingWithoutOne()
    {
        Assert.Equal(0f, RemotePlayerRenderer.ResolveSpeed(Player(alive: true)));
        Assert.Equal(4.5f, RemotePlayerRenderer.ResolveSpeed(Player(alive: true, moving: true, speed: 4.5f)));
        Assert.Equal(
            PlayerAnimationStateMachine.ReferenceWalkSpeed,
            RemotePlayerRenderer.ResolveSpeed(Player(alive: true, moving: true, speed: 0f)));
    }
}
