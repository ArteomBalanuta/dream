using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Actors;


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

    [Fact]
    public void CreateLocal_Rotates_PlayerMeshBackTowardThirdPersonCamera_WhenFacingForward()
    {
        PlayerRenderState state = PlayerRenderState.CreateLocal(
            Vector3.Zero,
            -Vector3.UnitZ,
            isMoving: false,
            scale: 1f);

        Vector3 actualMeshFront = TransformDirectionColumnMajor(
            state.ModelMatrix,
            Vector3.UnitZ).Normalized();

        Assert.Equal(0f, actualMeshFront.X, 3);
        Assert.Equal(0f, actualMeshFront.Y, 3);
        Assert.Equal(-1f, actualMeshFront.Z, 3);
    }

    private static Vector3 TransformDirectionColumnMajor(Matrix4 matrix, Vector3 direction)
    {
        return new Vector3(
            (matrix.M11 * direction.X) + (matrix.M21 * direction.Y) + (matrix.M31 * direction.Z),
            (matrix.M12 * direction.X) + (matrix.M22 * direction.Y) + (matrix.M32 * direction.Z),
            (matrix.M13 * direction.X) + (matrix.M23 * direction.Y) + (matrix.M33 * direction.Z));
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
