using OpenTK.Mathematics;
using Xunit;

public class PlayerRenderStateTests
{
    [Fact]
    public void CreateRemote_UsesConfiguredPlayerModelAndIdleClip()
    {
        RemotePlayerPresentationState player = CreateRemotePlayer(
            new Vector3(4f, 1.5f, 7f),
            -Vector3.UnitZ,
            isMoving: false);

        PlayerRenderState state = PlayerRenderState.CreateRemote(player);

        Assert.Equal(PlayerModelAssetDefinition.ModelPath, state.ModelPath);
        Assert.Equal(PlayerModelAssetDefinition.IdleClip, state.ClipName);
        Assert.Equal("enemy", state.ShaderName);
        Assert.Equal(4f, state.ModelMatrix.M41, 3);
        Assert.Equal(1.0f, state.ModelMatrix.M42, 3);
        Assert.Equal(7f, state.ModelMatrix.M43, 3);
    }

    [Fact]
    public void CreateRemote_UsesWalkClipWhenMoving()
    {
        RemotePlayerPresentationState player = CreateRemotePlayer(
            new Vector3(4f, 1.5f, 7f),
            -Vector3.UnitZ,
            isMoving: true);

        PlayerRenderState state = PlayerRenderState.CreateRemote(player);

        Assert.Equal(PlayerModelAssetDefinition.WalkClip, state.ClipName);
    }

    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f)]
    public void CreateRemote_RotatesAuthoredForwardTowardReplicatedFacing(float x, float y, float z)
    {
        Vector3 desiredForward = new(x, y, z);
        RemotePlayerPresentationState player = CreateRemotePlayer(Vector3.Zero, desiredForward, isMoving: false);

        PlayerRenderState state = PlayerRenderState.CreateRemote(player, scale: 1f);

        Vector3 actualForward = Vector4.TransformRow(
            new Vector4(PlayerModelAssetDefinition.AuthoredForwardAxis, 0f),
            state.ModelMatrix).Xyz.Normalized();

        Assert.Equal(desiredForward.X, actualForward.X, 3);
        Assert.Equal(desiredForward.Y, actualForward.Y, 3);
        Assert.Equal(desiredForward.Z, actualForward.Z, 3);
    }

    [Fact]
    public void CreateRemote_AddsHeadNodeOverrideFromReplicatedFacing()
    {
        RemotePlayerPresentationState player = CreateRemotePlayer(Vector3.Zero, Vector3.UnitX, isMoving: false);

        PlayerRenderState state = PlayerRenderState.CreateRemote(player);

        Assert.True(state.NodeOverrides.ContainsKey(PlayerModelAssetDefinition.HeadNodeName));
    }

    [Fact]
    public void CreateLocal_Uses_Player_Model_At_Local_Player_Position()
    {
        PlayerRenderState state = PlayerRenderState.CreateLocal(
            new Vector3(5f, 1.5f, 6f),
            -Vector3.UnitZ,
            isMoving: false);

        Assert.Equal(PlayerModelAssetDefinition.ModelPath, state.ModelPath);
        Assert.Equal(PlayerModelAssetDefinition.IdleClip, state.ClipName);
        Assert.Equal(5f, state.ModelMatrix.M41, 3);
        Assert.Equal(1.0f, state.ModelMatrix.M42, 3);
        Assert.Equal(6f, state.ModelMatrix.M43, 3);
    }

    [Fact]
    public void CreateLocal_GroundsVisualFeetAtPlayerBodyBottom()
    {
        Vector3 playerBodyCenter = new(5f, 1.5f, 6f);

        PlayerRenderState state = PlayerRenderState.CreateLocal(
            playerBodyCenter,
            -Vector3.UnitZ,
            isMoving: false);

        Assert.Equal(1.0f, state.ModelMatrix.M42, 3);
    }

    private static RemotePlayerPresentationState CreateRemotePlayer(
        Vector3 position,
        Vector3 facing,
        bool isMoving)
    {
        return new RemotePlayerPresentationState(
            7,
            "remote-1",
            "Remote One",
            new InterpolatedActorPose(position, facing),
            90,
            100,
            true,
            isMoving);
    }
}
