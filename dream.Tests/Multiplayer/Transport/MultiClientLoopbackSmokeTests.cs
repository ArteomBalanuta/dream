using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Transport;

// End-to-end smoke test over real loopback TCP: the real server service (tick loop, per-
// connection outbound queues, pacing) and two real client sessions (background writers),
// applying snapshots into ClientWorldSession like PlayScene does. It checks the architecture
// review fixes working together rather than unit by unit.
public class MultiClientLoopbackSmokeTests
{

    [Fact]
    public async Task TwoClients_AreBothPhysicsSimulatedAndKeepMatchingWorlds()
    {
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);
        int floorSize = host!.Runtime!.Map.Width;

        ClientUnderTest a = await ClientUnderTest.ConnectAsync(server.BoundPort, "session-a", floorSize);
        ClientUnderTest b = await ClientUnderTest.ConnectAsync(server.BoundPort, "session-b", floorSize);
        await using (a.Session)
        await using (b.Session)
        {
            Stopwatch clock = Stopwatch.StartNew();
            ulong sequence = 0;
            bool jumpSent = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                sequence++;
                // Both walk a little, then session-b jumps once: a jump only shows up in Y for a
                // physics player (secondary sessions used to be ghosts with a fixed height).
                bool walking = clock.Elapsed < TimeSpan.FromSeconds(0.6);
                // Time-based: loop iterations vary with timer granularity and snapshot work.
                bool jump = !jumpSent && clock.Elapsed > TimeSpan.FromSeconds(1.0);
                jumpSent |= jump;
                a.Session.QueueCommandFrame(CreateCommand("session-a", sequence, walking, jump: false));
                b.Session.QueueCommandFrame(CreateCommand("session-b", sequence, walking, jump));
                a.Drain();
                b.Drain();
                await Task.Delay(15);
            }

            a.Drain();
            b.Drain();
        }

        Assert.True(a.JoinAccepted && b.JoinAccepted, "Both clients should be accepted.");
        Assert.True(a.Snapshots > 60, $"session-a received only {a.Snapshots} snapshots.");
        Assert.True(b.Snapshots > 60, $"session-b received only {b.Snapshots} snapshots.");
        Assert.True(a.Moved("session-a") > 0.5f, $"session-a moved {a.Moved("session-a")}.");
        Assert.True(a.Moved("session-b") > 0.5f, $"session-b moved {a.Moved("session-b")}.");
        Assert.True(a.MaxY("session-b") > a.FirstY("session-b") + 0.3f, $"session-b never left the ground (max y {a.MaxY("session-b")}).");
        Assert.InRange(a.LastY("session-b"), a.FirstY("session-b") - 0.3f, a.FirstY("session-b") + 0.3f);
        Assert.True(a.ChecksumsVerified > 0 && b.ChecksumsVerified > 0, "No world checksum was verified.");
        Assert.Equal(0, a.ChecksumMismatches);
        Assert.Equal(0, b.ChecksumMismatches);
    }

    private static PlayerCommandFrame CreateCommand(string session, ulong sequence, bool walking, bool jump)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(session),
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(ForwardAxis: walking ? 1f : 0f, StrafeAxis: 0f, JumpPressed: jump),
            new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
            PrimaryUsePressed: false);
    }

    private sealed class ClientUnderTest
    {
        private readonly ClientWorldSession _world;
        private readonly Dictionary<string, List<Vector3>> _positions = new(StringComparer.Ordinal);

        private ClientUnderTest(RemoteClientSession session, ClientWorldSession world)
        {
            Session = session;
            _world = world;
        }

        public RemoteClientSession Session { get; }

        public bool JoinAccepted { get; private set; }

        public int Snapshots { get; private set; }

        public int ChecksumsVerified { get; private set; }

        public int ChecksumMismatches { get; private set; }

        public static async Task<ClientUnderTest> ConnectAsync(int port, string session, int floorSize)
        {
            RemoteClientSession remote = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", port, session, session, "smoke-test"));
            return new ClientUnderTest(
                remote,
                new ClientWorldSession(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false));
        }

        public void Drain()
        {
            while (Session.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
            {
                if (message is JoinAcceptedMessage accepted)
                {
                    JoinAccepted = true;
                    ClientJoinAcceptedApplier.ApplyToRuntime(_world, accepted);
                    continue;
                }

                if (message is not ServerSnapshotMessage snapshotMessage)
                {
                    continue;
                }

                ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                Snapshots++;
                RemoteWorldMutationApplier.ApplySnapshotToRuntime(_world, snapshot);
                if (snapshot.WorldChecksum.HasValue)
                {
                    ChecksumsVerified++;
                    if (RemoteWorldMutationApplier.IsWorldOutOfSync(_world, snapshot))
                    {
                        ChecksumMismatches++;
                    }
                }

                foreach (ActorSnapshot actor in snapshot.Actors.Where(actor => actor.Kind == CreatureKind.Player && actor.SessionId != null))
                {
                    if (!_positions.TryGetValue(actor.SessionId!, out List<Vector3>? trail))
                    {
                        trail = [];
                        _positions[actor.SessionId!] = trail;
                    }

                    trail.Add(actor.Position);
                }
            }
        }

        public float Moved(string session) => (Trail(session)[^1] - Trail(session)[0]).Xz.Length;

        public float FirstY(string session) => Trail(session)[0].Y;

        public float LastY(string session) => Trail(session)[^1].Y;

        public float MaxY(string session) => Trail(session).Max(position => position.Y);

        private List<Vector3> Trail(string session)
        {
            Assert.True(_positions.TryGetValue(session, out List<Vector3>? trail) && trail.Count > 1, $"No positions for {session}.");
            return trail!;
        }
    }
}
