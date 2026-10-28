using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// Wave 8 fix loop: a flat 5 s per frame would close a slow but live link that is receiving a big
// frame (a full world resync). Each write's stall limit is now SendStallTimeout plus the frame's
// size over ServerConnectionOptions.SendStallMinimumBytesPerSecond.
public class ServerConnectionSendStallScalingTests
{
    // About 100 KB of payload; with the envelope the frame is between 100,000 and 101,000 bytes.
    private const int ReasonLength = 100_000;

    [Fact]
    public void Default_AllowsAQuarterMegabytePerSecond()
    {
        Assert.Equal(256 * 1024, ServerConnectionOptions.Default.SendStallMinimumBytesPerSecond);
    }

    [Fact]
    public async Task LargeFrame_GetsItsTransferTime_OnTopOfTheStallTimeout()
    {
        ManualTimeProvider clock = new();
        // 10 KB/s: the ~100 KB frame gets a further 10 to 10.1 s over the 5 s base.
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.Zero,
            SendStallTimeout = TimeSpan.FromSeconds(5),
            SendStallMinimumBytesPerSecond = 10_000,
            TimeProvider = clock
        };
        (ServerConnection connection, GatedTransport transport) = await StartBlockedLargeWriteAsync(options);
        try
        {
            // Past the flat 5 s, well inside 5 s + transfer time.
            clock.Advance(TimeSpan.FromSeconds(14.5));
            Assert.False(connection.CheckLiveness());

            // Past 5 s + 10.1 s.
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(connection.CheckLiveness());
            Assert.Equal(ServerConnectionCloseReason.SendStall, connection.CloseReason);
        }
        finally
        {
            transport.Open();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SmallFrames_KeepTheFlatStallTimeout()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.Zero,
            SendStallTimeout = TimeSpan.FromSeconds(5),
            SendStallMinimumBytesPerSecond = 10_000,
            TimeProvider = clock
        };
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport transport = new("small");
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        try
        {
            transport.Close();
            // A rejection with a short reason: well under 1 KB, so under 0.1 s of allowance.
            connection.Enqueue(new JoinRejectedMessage("full"));
            Assert.True(await transport.SendStarted.WaitAsync(Deadline));

            clock.Advance(TimeSpan.FromSeconds(4.9));
            Assert.False(connection.CheckLiveness());
            clock.Advance(TimeSpan.FromSeconds(0.25));
            Assert.True(connection.CheckLiveness());
            Assert.Equal(ServerConnectionCloseReason.SendStall, connection.CloseReason);
        }
        finally
        {
            transport.Open();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task ZeroRate_GivesEveryFrameTheFlatStallTimeout()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.Zero,
            SendStallTimeout = TimeSpan.FromSeconds(5),
            SendStallMinimumBytesPerSecond = 0,
            TimeProvider = clock
        };
        (ServerConnection connection, GatedTransport transport) = await StartBlockedLargeWriteAsync(options);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(4.9));
            Assert.False(connection.CheckLiveness());
            clock.Advance(TimeSpan.FromSeconds(0.2));
            Assert.True(connection.CheckLiveness());
            Assert.Equal(ServerConnectionCloseReason.SendStall, connection.CloseReason);
        }
        finally
        {
            transport.Open();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task LargeFrameWrite_IsAllocationFreeToCheck()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.FromSeconds(10),
            SendStallTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = clock
        };
        (ServerConnection connection, GatedTransport transport) = await StartBlockedLargeWriteAsync(options);
        try
        {
            Dream.Tests.Utilities.AllocationAssert.AtMost(0, () => connection.CheckLiveness());
            Assert.False(connection.IsFaulted);
        }
        finally
        {
            transport.Open();
            await connection.DisposeAsync();
        }
    }

    // Queues one ~100 KB frame on a transport whose write never completes until opened.
    private static async Task<(ServerConnection Connection, GatedTransport Transport)> StartBlockedLargeWriteAsync(ServerConnectionOptions options)
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport transport = new("large", recordFrames: false);
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        transport.Close();
        connection.Enqueue(new JoinRejectedMessage(new string('x', ReasonLength)));
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        return (connection, transport);
    }
}
