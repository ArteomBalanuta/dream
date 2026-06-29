using OpenTK.Mathematics;
using Xunit;

public class RemotePlayerRendererTests
{
    [Fact]
    public void CreateModelMatrix_CentersRemotePlayerCubeAtInterpolatedPose()
    {
        RemotePlayerPresentationState remotePlayer = new(
            7,
            "remote-1",
            "Remote One",
            new InterpolatedActorPose(new Vector3(4f, 2f, 8f), -Vector3.UnitZ),
            90,
            100,
            true);

        Matrix4 model = RemotePlayerRenderer.CreateModelMatrix(remotePlayer);

        Assert.Equal(4f, model.M41, 4);
        Assert.Equal(2f, model.M42, 4);
        Assert.Equal(8f, model.M43, 4);
    }

    [Fact]
    public void SelectVisiblePlayers_FiltersDeadAndOffscreenRemotePlayers()
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

        RemotePlayerPresentationState selected = Assert.Single(visible);
        Assert.Equal("visible", selected.SessionId);
    }
}
