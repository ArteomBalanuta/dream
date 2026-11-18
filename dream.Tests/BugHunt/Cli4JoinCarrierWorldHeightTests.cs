using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.BugHunt;

// BH-XFER-6: the join carrier cross-checks its initial world's size against the descriptor, but
// only ever on Width and Depth. A height disagreement slipped through and sized the map from the
// descriptor while the cubes came from a world of another height.
public class Cli4JoinCarrierWorldHeightTests
{
    [Fact]
    public void AnInitialWorldOfAnotherHeight_IsAProtocolError()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptor descriptor = WorldDescriptor.ForFloorSize(12);
        WorldSnapshot world = new(12, descriptor.Height + 3, 12, Array.Empty<Vector3>());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(descriptor, world)));

        Assert.Contains("height", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, client.Map.Width);
    }

    [Fact]
    public void AnInitialWorldWhoseWidthDepthAndHeightAllAgree_IsAccepted()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptor descriptor = WorldDescriptor.ForFloorSize(12);
        WorldSnapshot world = new(12, descriptor.Height, 12, Array.Empty<Vector3>());

        ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(descriptor, world));

        Assert.Equal(12, client.Map.Width);
        Assert.Equal(12, client.Map.Depth);
    }

    // Controls: the Width and Depth checks this change sits beside are untouched.
    [Fact]
    public void AnInitialWorldOfAnotherWidth_IsStillAProtocolError()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptor descriptor = WorldDescriptor.ForFloorSize(12);
        WorldSnapshot world = new(10, descriptor.Height, 12, Array.Empty<Vector3>());

        Assert.Throws<InvalidDataException>(() =>
            ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(descriptor, world)));
        Assert.Equal(20, client.Map.Width);
    }

    [Fact]
    public void AnInitialWorldOfAnotherDepth_IsStillAProtocolError()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptor descriptor = WorldDescriptor.ForFloorSize(12);
        WorldSnapshot world = new(12, descriptor.Height, 10, Array.Empty<Vector3>());

        Assert.Throws<InvalidDataException>(() =>
            ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(descriptor, world)));
        Assert.Equal(20, client.Map.Width);
    }

    private static JoinAcceptedMessage Join(WorldDescriptor world, WorldSnapshot? initialWorld) =>
        new("session-a", 101, 0, initialWorld, World: WorldDescriptorWire.From(world));

    private static ClientWorldSession CreateClient(int floorSize) =>
        new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
}
