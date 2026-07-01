using System.Reflection;
using Xunit;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Players;


public class PlayerInteractionEdgeTests
{
    [Fact]
    public void TryCaptureBuildUseRequest_FiresOnlyOnRisingEdge_UntilRelease()
    {
        PlayerInteraction interaction = new();

        Assert.True(interaction.TryCaptureBuildUseRequest(
            new PlayerInteractionCommand(true),
            new Vector3(4f, 1.5f, 6f),
            Vector3.UnitX,
            out BuildUseRequest firstRequest));
        Assert.Equal(new Vector3(6f, 1.5f, 6f), firstRequest.PlacementTarget);

        Assert.False(interaction.TryCaptureBuildUseRequest(
            new PlayerInteractionCommand(true),
            new Vector3(100f, 100f, 100f),
            -Vector3.UnitX,
            out _));

        Assert.False(interaction.TryCaptureBuildUseRequest(
            new PlayerInteractionCommand(false),
            new Vector3(100f, 100f, 100f),
            Vector3.UnitY,
            out _));

        Assert.True(interaction.TryCaptureBuildUseRequest(
            new PlayerInteractionCommand(true),
            new Vector3(1f, 2f, 3f),
            new Vector3(0f, 0f, -1f),
            out BuildUseRequest secondRequest));
        Assert.Equal(new Vector3(1f, 2f, 1f), secondRequest.PlacementTarget);
    }

    [Fact]
    public void TryCaptureBuildUseRequest_StaysIdle_WhenButtonRemainsReleased()
    {
        PlayerInteraction interaction = new();

        Assert.False(interaction.TryCaptureBuildUseRequest(
            new PlayerInteractionCommand(false),
            new Vector3(4f, 1.5f, 6f),
            Vector3.UnitX,
            out BuildUseRequest request));
        Assert.Equal(default, request);

        Assert.False(interaction.TryCaptureBuildUseRequest(
            new PlayerInteractionCommand(false),
            new Vector3(1f, 2f, 3f),
            Vector3.UnitZ,
            out request));
        Assert.Equal(default, request);
    }

    [Fact]
    public void CreateBuildUseRequest_WhenDirectionIsZero_ReturnsFalse()
    {
        bool resolved = PlayerInteraction.TryCreateBuildUseRequest(
            interactionOrigin: new Vector3(4f, 1.5f, 6f),
            interactionDirection: Vector3.Zero,
            out BuildUseRequest request);

        Assert.False(resolved);
        Assert.Equal(default, request);
    }

    [Fact]
    public void CreateBuildUseRequest_ReturnsForwardOffsetTarget()
    {
        bool resolved = PlayerInteraction.TryCreateBuildUseRequest(
            interactionOrigin: new Vector3(4f, 1.5f, 6f),
            interactionDirection: new Vector3(0f, 0f, -1f),
            out BuildUseRequest request);

        Assert.True(resolved);
        Assert.Equal(new Vector3(4f, 1.5f, 4f), request.PlacementTarget);
    }

    [Fact]
    public void PublicBuildUseCapture_DoesNotDependOnCameraOrRigidBody()
    {
        MethodInfo[] publicMethods = typeof(PlayerInteraction).GetMethods(BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(publicMethods, method =>
            method.Name == nameof(PlayerInteraction.TryCaptureBuildUseRequest)
            && method.GetParameters().Any(parameter =>
                parameter.ParameterType.FullName is "Camera" or "BulletSharp.RigidBody"));
    }

    [Fact]
    public void AddCube_ReturnsAcceptedPlacementState()
    {
        Map map = new(loadVisualAssets: false, width: 4, height: 4, depth: 4);

        Assert.True(map.AddCube(new Vector3(1f, 1f, 1f), withPhysics: false));
        Assert.False(map.AddCube(new Vector3(1f, 1f, 1f), withPhysics: false));
        Assert.False(map.AddCube(new Vector3(-1f, 1f, 1f), withPhysics: false));
    }
}
