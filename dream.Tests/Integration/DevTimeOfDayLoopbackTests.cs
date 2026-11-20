using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// /dev tod over real loopback TCP: the jump reaches every joined client as a server.world-clock
// frame, the sender's before its [dev] reply, and a client that re-anchors on it reads the new time.
public class DevTimeOfDayLoopbackTests
{
    [Fact]
    public async Task DevTod_ReanchorsEveryJoinedClient_BeforeTheSendersReply()
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(persistence: null);
        host.DevCommandsAllowed = true;
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        await using WireLoopbackClient sender = await server.ConnectAndJoinAsync("tod-sender");
        await using WireLoopbackClient other = await server.ConnectAndJoinAsync("tod-other");
        await server.TickAsync();
        Assert.True(await server.FlushAllAsync());

        long worldTicksBefore = server.Runtime.WorldClock.WorldTicks;
        await sender.SendAsync(new ClientChatMessage(ChatChannel.Command, "/dev tod 21:30"));
        await server.UntilAsync(
            () => Replies(sender).Any() && Clocks(other).Any(),
            "the tod reply and the other client's clock message");

        Assert.Null(sender.ReadFault);
        Assert.Null(other.ReadFault);
        Assert.True(server.Runtime.WorldClock.WorldTicks >= worldTicksBefore);

        // The sender: exactly one clock frame, and it comes before the [dev] reply.
        List<IProtocolMessage> messages = sender.Frames.Select(frame => frame.Decoded.Message).ToList();
        ServerWorldClockMessage jump = Assert.Single(messages.OfType<ServerWorldClockMessage>());
        int clockIndex = messages.IndexOf(jump);
        int replyIndex = messages.FindIndex(m => m is ServerChatMessage chat && chat.Text.StartsWith(DevCommandText.ReplyPrefix, StringComparison.Ordinal));
        Assert.True(clockIndex < replyIndex, $"clock frame at {clockIndex}, reply at {replyIndex}");
        string reply = Assert.Single(Replies(sender));
        Assert.StartsWith(DevCommandText.ReplyPrefix + "World time set to 21:30 (day ", reply);

        // The anchor reads 21:30 to the tick on its own server tick.
        long ticksPerDay = jump.Anchor.TicksPerDay;
        long target = (long)Math.Round(21.5d / 24d * ticksPerDay) % ticksPerDay;
        double expected = target / (double)ticksPerDay;
        Assert.Equal(expected, jump.Anchor.TimeOfDay01At(jump.Anchor.ServerTick), 12);

        // A client that applies the join, then the jump, reads that time.
        ClientWorldClock clock = new();
        JoinAcceptedMessage join = Assert.Single(
            WorldTransferReassembler.Reassemble(sender.Frames.Select(frame => frame.Decoded.Message)).OfType<JoinAcceptedMessage>());
        clock.ApplyJoin(join.WorldClock);
        clock.ApplyJump(jump.Anchor);
        Assert.Equal(expected, clock.TimeOfDay01Exact(jump.Anchor.ServerTick), 12);
        Assert.Equal(WorldDayPhase.Night, clock.Phase(jump.Anchor.ServerTick));

        // The other client gets the same anchor, and no [dev] reply.
        Assert.Equal(jump, Assert.Single(Clocks(other)));
        Assert.Empty(Replies(other));
    }

    private static IEnumerable<ServerWorldClockMessage> Clocks(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<ServerWorldClockMessage>();

    private static IEnumerable<string> Replies(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<ServerChatMessage>()
            .Select(chat => chat.Text)
            .Where(text => text.StartsWith(DevCommandText.ReplyPrefix, StringComparison.Ordinal));
}
