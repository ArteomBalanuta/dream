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
[Collection(TimingSensitiveCollection.Name)]
public class MultiClientLoopbackSmokeTests
{
    // 0.6 s of walking at 60 ticks per second.
    private const ulong WalkTicks = 36;

    // Consecutive replicated positions (one per server tick) that must agree before a player
    // counts as at rest.
    private const int RestSamples = 15;

    [Fact]
    public async Task TwoClients_AreBothPhysicsSimulatedAndKeepMatchingWorlds()
    {
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);
        int floorSize = host!.Runtime!.Map.Width;

        using ClientUnderTest a = await ClientUnderTest.ConnectAsync(server.BoundPort, "session-a", floorSize);
        using ClientUnderTest b = await ClientUnderTest.ConnectAsync(server.BoundPort, "session-b", floorSize);
        await using (a.Session)
        await using (b.Session)
        {
            // Phases follow the server tick and the replicated state that session-a observes, not
            // the wall clock. Under a loaded test run this loop can stall for a second at a time
            // (snapshots then drain in bunches), so a wall-clock schedule sent the jump late and
            // stopped sampling while session-b was still in the air.
            Stopwatch deadline = Stopwatch.StartNew();
            ulong sequence = 0;
            ulong? walkStartTickA = null;
            ulong? walkStartTickB = null;
            bool jumpSent = false;
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                sequence++;
                // Each session's walk window starts once that session is joined and simulated:
                // session-a's at its first snapshot, session-b's at the first snapshot that
                // session-b receives showing itself. Anchoring session-b to session-a's first
                // snapshot let the window run out while session-b's join was still pending under
                // load; the server drops commands for an unjoined session and applies only the
                // newest command per session per tick, so session-b's walk frames were lost or
                // replaced by idle ones and it barely moved.
                walkStartTickA ??= a.LatestServerTick;
                walkStartTickB ??= b.FirstSelfServerTick;
                // Both walk a little, then session-b jumps once: a jump only shows up in Y for a
                // physics player (secondary sessions used to be ghosts with a fixed height).
                bool walkingA = walkStartTickA == null || a.LatestServerTick < walkStartTickA + WalkTicks;
                bool walkingB = walkStartTickB == null || a.LatestServerTick < walkStartTickB + WalkTicks;
                // Jump only once session-b has come to rest after the walk, so the jump starts
                // from the ground. The server latches JumpPressed until its next tick.
                bool jump = !jumpSent && !walkingB && a.IsAtRest("session-b", RestSamples);
                jumpSent |= jump;
                a.Session.QueueCommandFrame(CreateCommand("session-a", sequence, walkingA, jump: false));
                b.Session.QueueCommandFrame(CreateCommand("session-b", sequence, walkingB, jump));
                a.Drain();
                b.Drain();
                if (jumpSent &&
                    a.MaxY("session-b") > a.FirstY("session-b") + 0.3f &&
                    a.IsAtRest("session-b", RestSamples) &&
                    a.ChecksumsVerified > 0 &&
                    b.ChecksumsVerified > 0 &&
                    a.Snapshots > 60 &&
                    b.Snapshots > 60)
                {
                    // Jumped, landed and settled, with checksums seen on both clients.
                    break;
                }

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

    // Owns its ClientWorldSession (and so a native physics world), which Dispose releases.
    private sealed class ClientUnderTest : IDisposable
    {
        private readonly ClientWorldSession _world;
        private readonly Dictionary<string, List<Vector3>> _positions = new(StringComparer.Ordinal);

        private readonly string _sessionId;

        private ClientUnderTest(RemoteClientSession session, ClientWorldSession world)
        {
            Session = session;
            _world = world;
            _sessionId = session.SessionId;
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
                new ClientWorldSession(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub()));
        }

        public void Dispose()
        {
            _world.Dispose();
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
                LatestServerTick = snapshot.ServerTick;
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
                    if (actor.SessionId == _sessionId)
                    {
                        FirstSelfServerTick ??= snapshot.ServerTick;
                    }
                }
            }
        }

        public ulong? LatestServerTick { get; private set; }

        // The server tick of the first snapshot that showed this client's own player: from then
        // on the session is joined and its commands are simulated.
        public ulong? FirstSelfServerTick { get; private set; }

        // True once the last `samples` replicated positions of the session agree to within 1 cm.
        public bool IsAtRest(string session, int samples)
        {
            if (!_positions.TryGetValue(session, out List<Vector3>? trail) || trail.Count < samples)
            {
                return false;
            }

            Vector3 last = trail[^1];
            for (int i = trail.Count - samples; i < trail.Count; i++)
            {
                if ((trail[i] - last).Length > 0.01f)
                {
                    return false;
                }
            }

            return true;
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
