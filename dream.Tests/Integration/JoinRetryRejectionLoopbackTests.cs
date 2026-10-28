using System.Net;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Client.Networking;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// M-A over real loopback TCP: the server's join prefetch fails twice, so it answers with
// JoinRejected(JoinRetryRejectionReason) and keeps the connection open. The real client re-sends
// its join after the backoff and joins once the database reads again.
public class JoinRetryRejectionLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string SessionId = "session-retry";

    [Fact]
    public async Task RetryRejection_FromAFailingPrefetch_IsRetriedByTheClient_WhichJoinsOnceTheDatabaseRecovers()
    {
        using PersistenceTestDatabase database = new();
        FailingReadProfiles profiles = new(new SqlitePlayerProfileRepository(database.Schema)) { FailReads = true };
        using DedicatedServerHost host = CreateHost(profiles, new SqliteWorldPersistenceRepository(database.Schema));
        await using DedicatedServerService service = new(
            host,
            new TcpServerTransport(IPAddress.Loopback, 0),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());
        service.Start();

        using CancellationTokenSource stopTicks = new();
        Task tickPump = PumpTicksAsync(service, stopTicks.Token);
        ManualTimerTimeProvider clock = new();
        try
        {
            Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
            await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", service.Port, SessionId, "Retry Player", "retry-client"),
                timeProvider: clock);
            ServerConnection connection = await accept.WaitAsync(Timeout);

            // Load failed, restarted once, failed again: rejected for retry, and the client is
            // now waiting out its first backoff.
            await WaitUntilAsync(() => clock.HasTimerDueIn(RemoteClientSession.JoinRetryBaseDelay), "the client's retry backoff");
            Assert.True(session.IsConnected);
            Assert.False(session.IsJoined);
            Assert.False(host.IsConnectionJoined(connection.ConnectionId));

            profiles.FailReads = false;
            clock.Advance(RemoteClientSession.JoinRetryBaseDelay);
            await WaitUntilAsync(() => session.IsJoined, "the retried join");

            Assert.True(session.IsConnected);
            Assert.Null(session.DisconnectReason);
            Assert.Equal(1, session.JoinRetryCount);
            await WaitUntilAsync(() => host.IsConnectionJoined(connection.ConnectionId), "the server to mark the connection joined");
            Assert.Equal(SessionId, host.Runtime!.PrimaryRemoteSessionId?.Value);

            List<IServerToClientMessage> inbound = [];
            while (session.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
            {
                inbound.Add(message);
            }

            int rejectedIndex = inbound.FindIndex(message => message is JoinRejectedMessage);
            int acceptedIndex = inbound.FindIndex(message => message is JoinAcceptedMessage);
            Assert.True(rejectedIndex >= 0, "The client never received the retry rejection.");
            Assert.Equal(DedicatedServerHost.JoinRetryRejectionReason, ((JoinRejectedMessage)inbound[rejectedIndex]).Reason);
            Assert.True(acceptedIndex > rejectedIndex, "JoinAccepted did not follow the rejection.");
            Assert.Single(inbound.OfType<JoinRejectedMessage>());
        }
        finally
        {
            stopTicks.Cancel();
            await tickPump;
        }
    }

    // The service's tick loop, run by the test so it stops before the service is disposed.
    private static async Task PumpTicksAsync(DedicatedServerService service, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            try
            {
                await Task.Delay(5, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static DedicatedServerHost CreateHost(IPlayerProfileRepository profiles, IWorldPersistenceRepository world)
    {
        WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            worldPersistence: world,
            persistenceWrites: PersistenceWriteQueue.Background());
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(runtime), router, deferJoinsToTick: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    // Fails every profile read while FailReads is set.
    private sealed class FailingReadProfiles(IPlayerProfileRepository inner) : IPlayerProfileRepository
    {
        public volatile bool FailReads;

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            ThrowIfFailing();
            return inner.GetOrCreate(playerName);
        }

        public PersistedPlayerProfile? TryGet(string playerName)
        {
            ThrowIfFailing();
            return inner.TryGet(playerName);
        }

        public void Save(PersistedPlayerProfile profile) => inner.Save(profile);

        private void ThrowIfFailing()
        {
            if (FailReads)
            {
                throw new InvalidOperationException("database is locked (test)");
            }
        }
    }
}
