using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// Wave 8 fix loop: TryTakeOverflowResync clears the flag before the host's cooldown decides, so a
// refused overflow resync used to be lost. The service re-arms it on a live connection.
public class OverflowResyncRearmTests
{
    [Fact]
    public void RearmOverflowResync_MakesTheNextTakeSucceedOnce()
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        ServerConnection connection = new(new GatedTransport("rearm"), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);

        Assert.False(connection.TryTakeOverflowResync());
        connection.RearmOverflowResync();
        Assert.True(connection.TryTakeOverflowResync());
        Assert.False(connection.TryTakeOverflowResync());
    }

    [Fact]
    public async Task RefusedResync_IsRearmedOnALiveConnection_AcceptedIsNot()
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        ServerConnection connection = new(new GatedTransport("live"), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        service.AttachConnection(connection);

        Assert.False(service.HandleOverflowResyncResult(connection, accepted: true));
        Assert.False(connection.TryTakeOverflowResync());

        Assert.True(service.HandleOverflowResyncResult(connection, accepted: false));
        Assert.True(connection.TryTakeOverflowResync());
    }

    [Fact]
    public async Task RefusedResync_IsNotRearmedForAFaultedOrRemovedConnection()
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);

        // Never attached (or already removed).
        await using ServerConnection detached = new(new GatedTransport("gone"), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        Assert.False(service.HandleOverflowResyncResult(detached, accepted: false));
        Assert.False(detached.TryTakeOverflowResync());

        // Attached but faulted (backlog overflow behind a blocked write).
        GatedTransport transport = new("faulted", recordFrames: false);
        transport.Close();
        ServerConnection faulted = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        service.AttachConnection(faulted);
        for (int index = 0; index <= ServerConnection.MaxPendingOutboundMessages + 1; index++)
        {
            faulted.Enqueue(new ServerActionMessage(new AuthoritativeActionResult("session-a", (ulong)index, 1, true, null)));
        }

        Assert.True(faulted.IsFaulted);
        Assert.False(service.HandleOverflowResyncResult(faulted, accepted: false));
        Assert.False(faulted.TryTakeOverflowResync());
        transport.Open();
    }
}
