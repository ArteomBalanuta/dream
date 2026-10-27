using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// A session that leaves the dedicated server (ReleaseConnection) and joins again under the same
// session id, as a reconnecting client does. WorldSessionRuntime.AttachRemoteSession handles a
// returning session (RemoteSessionPlayerStateTests), but the server join path,
// RuntimeAuthoritativeSessionHost.JoinSession, takes its own existing-session branch: it marks the
// player spawned and answers JoinAccepted without attaching it to the runtime again. Found by the
// MEM-3/MEM-9/NET-7 long-run churn test (LongRunServerChurnIntegrationTests).
public class SessionRejoinIntegrationTests
{
    private const string Bug =
        "Production bug: RuntimeAuthoritativeSessionHost.JoinSession (RuntimeAuthoritativeSessionHost.cs:52-68) " +
        "handles a returning session by MarkSpawned only and never calls WorldSessionRuntime.AttachRemoteSession, " +
        "so a rejoined secondary has no RemoteSessionPlayerState and a rejoined former primary is never re-seated.";

    private const string SessionA = "session-a";
    private const string SessionB = "session-b";

    [Fact(Skip = Bug)]
    public void Secondary_LeavesAndRejoinsOnANewConnection_IsListedAndMovesAgain()
    {
        using Server server = new();
        server.Join(SessionA, "a-1");
        server.Join(SessionB, "b-1");
        server.Ticks(30, SessionA, SessionB);
        // Control: before leaving, the same commands move the secondary.
        Vector3 start = server.Actor(SessionB)!.Value.Position;
        server.Ticks(30, SessionA, SessionB, moveSession: SessionB);
        Assert.True((server.Actor(SessionB)!.Value.Position - start).Length > 0.5f, "The secondary does not move before leaving.");
        int actorIdBefore = server.Actor(SessionB)!.Value.ActorId;

        server.Leave("b-1");
        server.Ticks(10, SessionA);
        Assert.Null(server.Actor(SessionB));

        JoinAcceptedMessage accepted = server.Join(SessionB, "b-2");
        server.Ticks(1, SessionA, SessionB);

        Assert.Equal(actorIdBefore, accepted.ActorId);
        ActorSnapshot rejoined = Assert.IsType<ActorSnapshot>(server.Actor(SessionB));
        Assert.Equal(accepted.ActorId, rejoined.ActorId);
        Assert.True(server.Runtime.TryResolveCombatActor(accepted.ActorId, out _));

        // Its commands drive a body again.
        Vector3 before = rejoined.Position;
        server.Ticks(30, SessionA, SessionB, moveSession: SessionB);
        Assert.True((server.Actor(SessionB)!.Value.Position - before).Length > 0.5f, "The rejoined player does not move.");
    }

    [Fact(Skip = Bug)]
    public void FormerPrimary_LeavesAndRejoinsWhileTheSeatIsFree_RetakesTheSeat()
    {
        using Server server = new();
        server.Join(SessionA, "a-1");
        server.Join(SessionB, "b-1");
        server.Ticks(30, SessionA, SessionB);

        server.Leave("a-1");
        server.Ticks(10, SessionB);
        Assert.Null(server.Runtime.PrimaryRemoteSessionId);

        server.Join(SessionA, "a-2");
        server.Ticks(1, SessionA, SessionB);

        Assert.Equal(SessionA, server.Runtime.PrimaryRemoteSessionId?.Value);
        Assert.False(server.Runtime.Physics.IsPlayerBodyParked);
        ActorSnapshot primary = server.Runtime.CaptureActorSnapshots()[0];
        Assert.Equal(SessionA, primary.SessionId);
    }

    private sealed class Server : IDisposable
    {
        private readonly DedicatedServerHost _host;
        private readonly Dictionary<string, string> _connections = new(StringComparer.Ordinal);
        private ulong _sequence;

        public Server()
        {
            Runtime = new WorldSessionRuntime(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false);
            // No wolf: the players must not be pushed or killed.
            Runtime.Enemies.Clear();
            ProtocolRouter router = new(
                [new ClientHelloHandler(), new JoinRequestHandler(), new PlayerCommandHandler(), new PlayerActionHandler()],
                Array.Empty<IClientMessageHandler>());
            _host = new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(Runtime), router);
        }

        public WorldSessionRuntime Runtime { get; }

        public JoinAcceptedMessage Join(string session, string connection)
        {
            _connections[session] = connection;
            IReadOnlyList<IServerToClientMessage> replies = _host.HandleClientMessage(connection, new ClientJoinRequestMessage(session, session));
            return Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies));
        }

        public void Leave(string connection)
        {
            _host.ReleaseConnection(connection);
        }

        public ActorSnapshot? Actor(string session)
        {
            foreach (ActorSnapshot actor in Runtime.CaptureActorSnapshots())
            {
                if (actor.SessionId == session)
                {
                    return actor;
                }
            }

            return null;
        }

        public void Ticks(int count, string first, string? second = null, string? moveSession = null)
        {
            for (int i = 0; i < count; i++)
            {
                _sequence++;
                foreach (string? session in new[] { first, second })
                {
                    if (session == null)
                    {
                        continue;
                    }

                    float forward = session == moveSession ? 1f : 0f;
                    _host.HandleClientMessage(_connections[session], new PlayerCommandMessage(new PlayerCommandFrame(
                        new PlayerSessionId(session),
                        _sequence,
                        new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                        new PlayerMoveIntent(0f, forward),
                        new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
                        PrimaryUsePressed: false)));
                }

                int executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
                if (executed == 0)
                {
                    executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
                }

                Assert.Equal(1, executed);
                _host.DrainPendingActionResults();
                _ = _host.CreateSnapshotMessage();
            }
        }

        public void Dispose()
        {
            _host.Dispose();
        }
    }
}
