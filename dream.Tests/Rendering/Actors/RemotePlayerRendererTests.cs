using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Rendering.Actors;


public class RemotePlayerRendererTests
{
    [Fact]
    public void CreateRemoteRenderState_GroundsPlayerModelAtInterpolatedPose()
    {
        RemotePlayerPresentationState remotePlayer = new(
            7,
            "remote-1",
            "Remote One",
            new InterpolatedActorPose(new Vector3(4f, 2f, 8f), -Vector3.UnitZ),
            90,
            100,
            true);

        Matrix4 model = PlayerRenderState.CreateRemote(remotePlayer).ModelMatrix;

        Assert.Equal(4f, model.M41, 4);
        Assert.Equal(1.5f, model.M42, 4);
        Assert.Equal(8f, model.M43, 4);
    }

    [Fact]
    public void SelectVisiblePlayers_OnlyFiltersDeadOrTooDistantRemotePlayers()
    {
        Camera camera = new(new Vector3(0f, 2f, 0f));
        List<RemotePlayerPresentationState> players =
        [
            new(
                1,
                "visible",
                "Visible",
                new InterpolatedActorPose(new Vector3(0f, 2f, -4f), -Vector3.UnitZ),
                100,
                100,
                true),
            new(
                2,
                "dead",
                "Dead",
                new InterpolatedActorPose(new Vector3(0f, 2f, -5f), -Vector3.UnitZ),
                0,
                100,
                false),
                new(
                3,
                "behind",
                "Behind",
                new InterpolatedActorPose(new Vector3(0f, 2f, 4f), Vector3.UnitZ),
                100,
                100,
                true)
        ];

        IReadOnlyList<RemotePlayerPresentationState> visible = RemotePlayerRenderer.SelectVisiblePlayers(camera, players);

        Assert.Collection(
            visible.OrderBy(player => player.SessionId),
            first => Assert.Equal("behind", first.SessionId),
            second => Assert.Equal("visible", second.SessionId));
    }

    [Fact]
    public void SelectVisiblePlayers_FiltersRemotePlayersOutsideRenderDistance()
    {
        Camera camera = new(new Vector3(0f, 2f, 0f));
        List<RemotePlayerPresentationState> players =
        [
            new(
                1,
                "far",
                "Far Away",
                new InterpolatedActorPose(new Vector3(0f, 2f, WorldVisibility.MaxRenderDistance + 10f), Vector3.UnitZ),
                100,
                100,
                true)
        ];

        IReadOnlyList<RemotePlayerPresentationState> visible = RemotePlayerRenderer.SelectVisiblePlayers(camera, players);

        Assert.Empty(visible);
    }
}
