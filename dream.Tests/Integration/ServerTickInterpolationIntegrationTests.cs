using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Integration;

// MEM-4 rewrote the host tick pipeline (DedicatedServerHost, RuntimeAuthoritativeSessionHost,
// command inbox) without allocations; NET-3 keyed remote interpolation by server tick. This runs
// the real host tick loop for two moving sessions, ships every snapshot through the wire codec,
// and feeds each client's ServerTickClock and ClientScenePresenter frame by frame, including a
// burst of three snapshots drained in one frame.
public class ServerTickInterpolationIntegrationTests(ITestOutputHelper output)
{
    // Both players spawn on the same point, so B first walks off along +Z and everyone settles;
    // only the ticks after that are measured, so player-player collision cannot push either one.
    private const ulong WarmupMoveTicks = 20;
    private const ulong WarmupTicks = 40;
    private const int ServerTicks = 24;
    private const int TrailingFrames = 16;
    private const float MonotonicTolerance = 1e-4f;
    private static readonly HashSet<ulong> BurstHeldTicks = [WarmupTicks + 10, WarmupTicks + 11];
    private const ulong BurstReleaseTick = WarmupTicks + 12;

    [Fact]
    public void TwoMovingSessions_RemotePositionsInterpolateMonotonically_ThroughABurst()
    {
        SceneEventHub events = new();
        // A 10-cell floor has no startup terrain; larger maps put landmark cubes next to the spawn point.
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost authority = new(runtime);
        using DedicatedServerHost host = CreateHost(authority);
        TransportJsonCodec codec = new();

        ClientView viewA = Join(host, "connection-a", "session-a", "Player A");
        ClientView viewB = Join(host, "connection-b", "session-b", "Player B");
        // Session A joined before B existed; B's join already shows A as a remote player.
        Assert.Equal("session-a", viewB.RemoteSessionId(Assert.Single(viewB.Presenter.BuildRemotePlayerStates(viewB.RenderTick))));

        Dictionary<string, List<Vector3>> serverPositions = new(StringComparer.Ordinal)
        {
            ["session-a"] = [],
            ["session-b"] = []
        };
        Dictionary<string, Vector3> measuredStart = new(StringComparer.Ordinal);
        List<ServerSnapshotMessage> held = [];
        int burstFrame = -1;
        int frame = 0;
        ulong lastTick = WarmupTicks + ServerTicks;
        for (ulong tick = 1; tick <= lastTick; tick++, frame++)
        {
            bool measuring = tick > WarmupTicks;
            if (measuring)
            {
                host.HandleClientMessage("connection-a", Move("session-a", tick, Vector3.UnitX));
                host.HandleClientMessage("connection-b", Move("session-b", tick, -Vector3.UnitX));
            }
            else
            {
                host.HandleClientMessage("connection-a", Idle("session-a", tick));
                host.HandleClientMessage(
                    "connection-b",
                    tick <= WarmupMoveTicks ? Move("session-b", tick, Vector3.UnitZ) : Idle("session-b", tick));
            }

            Assert.Equal(1, host.AdvanceTime(SimulationClock.FixedDeltaSeconds));

            ServerSnapshotMessage sent = host.CreateSnapshotMessage();
            Assert.Equal(tick, sent.Snapshot.ServerTick);
            ServerSnapshotMessage received = RoundTrip(codec, sent, tick);
            foreach (ActorSnapshot actor in received.Snapshot.Actors)
            {
                if (actor.SessionId is string sessionId && serverPositions.TryGetValue(sessionId, out List<Vector3>? positions))
                {
                    if (measuring)
                    {
                        positions.Add(actor.Position);
                    }
                    else if (tick == WarmupTicks)
                    {
                        measuredStart[sessionId] = actor.Position;
                    }
                }
            }

            viewA.Advance(SimulationClock.FixedDeltaSeconds);
            viewB.Advance(SimulationClock.FixedDeltaSeconds);
            if (BurstHeldTicks.Contains(tick))
            {
                held.Add(received);
            }
            else if (tick == BurstReleaseTick)
            {
                // Same-frame burst: three consecutive ticks all arrive now.
                held.Add(received);
                Assert.Equal(3, held.Count);
                foreach (ServerSnapshotMessage message in held)
                {
                    viewA.Deliver(message);
                    viewB.Deliver(message);
                }

                held.Clear();
                burstFrame = frame;
            }
            else
            {
                viewA.Deliver(received);
                viewB.Deliver(received);
            }

            // Record from the first measured frame; the render tick lags, so the first samples
            // still show the settled warm-up position, which is the measured path's start.
            viewA.Render(frame, record: measuring);
            viewB.Render(frame, record: measuring);
        }

        for (int trailing = 0; trailing < TrailingFrames; trailing++, frame++)
        {
            viewA.Advance(SimulationClock.FixedDeltaSeconds);
            viewB.Advance(SimulationClock.FixedDeltaSeconds);
            viewA.Render(frame, record: true);
            viewB.Render(frame, record: true);
        }

        output.WriteLine($"start a={measuredStart["session-a"]} b={measuredStart["session-b"]}");
        for (int i = 0; i < serverPositions["session-a"].Count; i++)
        {
            output.WriteLine($"  server tick {WarmupTicks + (ulong)i + 1}: a={serverPositions["session-a"][i]} b={serverPositions["session-b"][i]}");
        }

        Assert.True(burstFrame > 0);
        // B left the shared spawn point during warm-up.
        Assert.True((measuredStart["session-b"] - measuredStart["session-a"]).Length > 1f);
        // Server-side truth: A moves +X and B moves -X every measured tick.
        AssertMonotonic(serverPositions["session-a"], Vector3.UnitX, "server session-a");
        AssertMonotonic(serverPositions["session-b"], -Vector3.UnitX, "server session-b");
        Assert.True(serverPositions["session-a"][^1].X - measuredStart["session-a"].X > 0.5f);
        Assert.True(measuredStart["session-b"].X - serverPositions["session-b"][^1].X > 0.5f);

        // Client A sees B, client B sees A.
        AssertPresentation(viewA, "session-b", -Vector3.UnitX, measuredStart["session-b"], serverPositions["session-b"]);
        AssertPresentation(viewB, "session-a", Vector3.UnitX, measuredStart["session-a"], serverPositions["session-a"]);
    }

    private void AssertPresentation(ClientView view, string remoteSession, Vector3 direction, Vector3 start, List<Vector3> serverPath)
    {
        List<(int Frame, double RenderTick, Vector3 Position)> samples = view.Samples[remoteSession];
        output.WriteLine($"{view.LocalSessionId} sees {remoteSession}:");
        foreach ((int frame, double renderTick, Vector3 position) in samples)
        {
            output.WriteLine($"  frame {frame,2} renderTick {renderTick,7:F3} x={position.X:F4}");
        }

        Assert.True(samples.Count >= ServerTicks);
        double previousRenderTick = double.NegativeInfinity;
        for (int i = 0; i < samples.Count; i++)
        {
            // The render tick itself never runs backwards, or every remote snaps back with it.
            Assert.True(
                samples[i].RenderTick >= previousRenderTick - 1e-9,
                $"{view.LocalSessionId}: render tick went backwards at frame {samples[i].Frame}: {previousRenderTick:F4} -> {samples[i].RenderTick:F4}");
            previousRenderTick = samples[i].RenderTick;
        }

        AssertMonotonic(samples.Select(sample => sample.Position).ToList(), direction, $"{view.LocalSessionId} view of {remoteSession}");

        // Presented positions stay on the server's path, never beyond its newest sample.
        float minX = Math.Min(serverPath.Min(p => p.X), start.X);
        float maxX = Math.Max(serverPath.Max(p => p.X), start.X);
        Assert.All(samples, sample => Assert.InRange(sample.Position.X, minX - MonotonicTolerance, maxX + MonotonicTolerance));

        // It ends at the last authoritative position once the render tick has caught up.
        Assert.Equal(serverPath[^1].X, samples[^1].Position.X, 3);
        // And it actually interpolated: some frames fall strictly between two server samples.
        int between = samples.Count(sample => serverPath.All(p => MathF.Abs(p.X - sample.Position.X) > 1e-3f));
        Assert.True(between > 0, "No presented position fell between two server samples.");
    }

    private static void AssertMonotonic(List<Vector3> positions, Vector3 direction, string label)
    {
        for (int i = 1; i < positions.Count; i++)
        {
            float step = Vector3.Dot(positions[i] - positions[i - 1], direction);
            Assert.True(
                step >= -MonotonicTolerance,
                $"{label}: position moved backwards at sample {i}: {positions[i - 1]} -> {positions[i]}");
        }
    }

    private static ServerSnapshotMessage RoundTrip(TransportJsonCodec codec, ServerSnapshotMessage message, ulong sequence)
    {
        byte[] payload = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);
        return Assert.IsType<ServerSnapshotMessage>(codec.Decode(payload).Message);
    }

    private static ClientView Join(DedicatedServerHost host, string connectionId, string sessionId, string displayName)
    {
        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage(
            connectionId,
            new ClientJoinRequestMessage(sessionId, displayName));
        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies, reply => reply is JoinAcceptedMessage));
        Assert.Equal(sessionId, accepted.SessionId);
        ClientView view = new(sessionId);
        view.Join(accepted);
        return view;
    }

    private static PlayerCommandMessage Move(string sessionId, ulong sequence, Vector3 facing)
    {
        return new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId(sessionId),
                sequence,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
                new PlayerLookIntent(0f, 0f, facing),
                PrimaryUsePressed: false));
    }

    private static PlayerCommandMessage Idle(string sessionId, ulong sequence)
    {
        return new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId(sessionId),
                sequence,
                PlayerModeSwitchIntent.None,
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false));
    }

    private static DedicatedServerHost CreateHost(ITickDrivenAuthoritativeSessionHost authority)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(authority, router);
    }

    // The PlayScene order: advance the clock in update, observe and apply each received snapshot,
    // then sample remotes at EstimatedTick - InterpolationDelayTicks when rendering.
    private sealed class ClientView(string localSessionId)
    {
        private readonly ServerTickClock _clock = new();

        public string LocalSessionId { get; } = localSessionId;

        public ClientScenePresenter Presenter { get; } = new(localSessionId);

        public Dictionary<string, List<(int Frame, double RenderTick, Vector3 Position)>> Samples { get; } = new(StringComparer.Ordinal);

        public double RenderTick => _clock.EstimatedTick - _clock.InterpolationDelayTicks;

        public void Join(JoinAcceptedMessage accepted)
        {
            Presenter.ApplyJoinAccepted(accepted);
            _clock.Reset(accepted.CurrentServerTick);
        }

        public string RemoteSessionId(RemotePlayerPresentationState state) => state.SessionId;

        public void Advance(double seconds) => _clock.Advance(seconds);

        public void Deliver(ServerSnapshotMessage message)
        {
            _clock.Observe(message.Snapshot.ServerTick);
            Presenter.ApplySnapshot(message.Snapshot);
        }

        public void Render(int frame, bool record)
        {
            double renderTick = RenderTick;
            foreach (RemotePlayerPresentationState state in Presenter.BuildRemotePlayerStates(renderTick))
            {
                Assert.NotEqual(LocalSessionId, state.SessionId);
                if (!record)
                {
                    continue;
                }

                if (!Samples.TryGetValue(state.SessionId, out List<(int, double, Vector3)>? list))
                {
                    list = [];
                    Samples[state.SessionId] = list;
                }

                list.Add((frame, renderTick, state.Pose.Position));
            }
        }
    }
}
