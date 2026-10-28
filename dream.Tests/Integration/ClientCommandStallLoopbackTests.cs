using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// NETR-4 end to end: the real dedicated server over loopback TCP and a headless client that runs
// PlayScene's per-tick loop (ClientCommandTickStream, ClientWorldSession prediction, the
// reconciler and ClientCorrectionApplier). The client walks, stalls for one second, resumes, and
// the test reports the largest correction the client applied after the stall.
//
// Two stalls:
// - Send stall: the client keeps predicting and queueing, but its transport blocks for 1 s; the
//   bounded outbound queue merges its oldest commands and the backlog arrives in a burst.
// - Frame stall: the whole client loop freezes for 1 s; on resume the tick stream catches up at
//   most SimulationClock.DefaultMaxTicksPerAdvance ticks and drops the rest.
//
// The distances are reported, not asserted: they depend on the server inbox (latest-only today,
// NETR-5's buffered inbox later). The assertions cover the command stream itself.
//
// Wave 8 fix loop 1 (the harness now calls ObserveSendBacklog as PlayScene does), Debug, loopback,
// three runs: send stall 0.000 m after the stall, no snap (was 5.7-8.2 m before the client idled
// past the repeat window); frame stall 0.834 m smoothed, no snap.
public class ClientCommandStallLoopbackTests(ITestOutputHelper output)
{
    private const string SessionId = "stall-walker";
    private const int WalkTicksBeforeStall = 32;
    private const int StallTicks = SimulationClock.AuthoritativeTicksPerSecond;
    private const int TicksAfterStall = 64;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SendStall_OneSecond_SendsInOrderAndReportsTheCorrection()
    {
        StallReport report = await RunAsync(StallKind.Send);

        output.WriteLine(report.Describe("1 s send stall") + $"; wire sequence gaps {CountGaps(report.SentSequences)}");
        Assert.True(report.MaxCorrectionBefore < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("send"));
        // Strictly increasing on the wire. The stall overflows the outbound queue, which folds runs
        // of equal movement: once the client idles past the repeat window the backlog is one folded
        // walk frame plus folded idle frames, so at most two sequence jumps.
        AssertIncreasing(report.SentSequences);
        Assert.InRange(CountGaps(report.SentSequences), 1, 2);
        Assert.True(report.MergedCommands > 0, "The stall never overflowed the outbound queue.");
        Assert.True(report.LastAcknowledged >= report.StallEndTick, "No snapshot acknowledged a command sent after the stall.");
        Assert.True(report.ServerWalked > 1f, $"The walker only moved {report.ServerWalked} m on the server.");
    }

    [Fact]
    public async Task FrameStall_OneSecond_CatchesUpWithConsecutiveCommandsAndReportsTheCorrection()
    {
        StallReport report = await RunAsync(StallKind.Frame);

        output.WriteLine(report.Describe("1 s frame stall"));
        Assert.True(report.MaxCorrectionBefore < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("frame"));
        AssertIncreasing(report.SentSequences);
        Assert.Equal(0, CountGaps(report.SentSequences));
        Assert.True(report.DroppedTicks > 0, "The frame stall never hit the catch-up cap.");
        Assert.True(report.LastAcknowledged >= report.StallEndTick, "No snapshot acknowledged a command sent after the stall.");
        Assert.True(report.ServerWalked > 1f, $"The walker only moved {report.ServerWalked} m on the server.");
    }

    private enum StallKind
    {
        Send,
        Frame
    }

    private async Task<StallReport> RunAsync(StallKind stallKind)
    {
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);
        int floorSize = host!.Runtime!.Map.Width;
        Vector3 facing = MostOpenFacing(floorSize);

        StallableTransport transport = new();
        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, SessionId, SessionId, "stall-test"),
            transport);
        await using (session)
        {
            using HeadlessClient client = new(session, floorSize, facing);
            Stopwatch clock = Stopwatch.StartNew();
            double lastSeconds = 0d;
            ulong? walkStartTick = null;
            bool stalled = false;
            bool stallDone = false;
            while (clock.Elapsed < Timeout)
            {
                double now = clock.Elapsed.TotalSeconds;
                double elapsed = now - lastSeconds;
                lastSeconds = now;
                client.Drain();
                if (client.SelfSeen)
                {
                    walkStartTick ??= client.Ticks.LastTick;
                    ulong walked = client.Ticks.LastTick - walkStartTick.Value;
                    if (!stalled && walked >= WalkTicksBeforeStall)
                    {
                        stalled = true;
                        client.StallStartTick = client.Ticks.LastTick;
                        if (stallKind == StallKind.Frame)
                        {
                            // The whole client loop freezes: no input, no prediction, no sends.
                            await Task.Delay(TimeSpan.FromSeconds(1));
                        }
                        else
                        {
                            transport.Connection!.Stall();
                        }
                    }

                    if (stallKind == StallKind.Send && stalled && !stallDone && walked >= WalkTicksBeforeStall + StallTicks)
                    {
                        transport.Connection!.Resume();
                        stallDone = true;
                        client.StallEndTick = client.Ticks.LastTick + 1;
                    }

                    client.Frame(elapsed);
                    if (stallKind == StallKind.Frame && stalled && !stallDone)
                    {
                        // This frame's advance was the catch-up after the freeze.
                        stallDone = true;
                        client.StallEndTick = client.StallStartTick + 1;
                    }

                    if (stallDone && client.Ticks.LastTick >= client.StallEndTick + TicksAfterStall)
                    {
                        break;
                    }
                }
                else
                {
                    elapsed = 0d;
                }

                await Task.Delay(1);
            }

            Assert.True(stallDone, $"The scenario did not finish in {Timeout.TotalSeconds} s (self seen: {client.SelfSeen}).");
            transport.Connection!.Resume();
            await session.FlushOutboundAsync(TimeSpan.FromSeconds(5));
            return client.CreateReport(transport.Connection.SentCommandSequences(), session.MergedCommandCount);
        }
    }

    private static void AssertIncreasing(IReadOnlyList<ulong> sequences)
    {
        Assert.NotEmpty(sequences);
        for (int i = 1; i < sequences.Count; i++)
        {
            Assert.True(sequences[i] > sequences[i - 1], $"sequence {sequences[i]} after {sequences[i - 1]}");
        }
    }

    private static int CountGaps(IReadOnlyList<ulong> sequences)
    {
        int gaps = 0;
        for (int i = 1; i < sequences.Count; i++)
        {
            if (sequences[i] != sequences[i - 1] + 1)
            {
                gaps++;
            }
        }

        return gaps;
    }

    // The startup terrain blocks some directions; walk along the most open one, probed on the
    // same startup world the server runs.
    private static Vector3 MostOpenFacing(int floorSize)
    {
        const int ProbeTicks = WalkTicksBeforeStall + StallTicks + TicksAfterStall + 16;
        return new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }
            .OrderByDescending(direction =>
            {
                WorldSessionRuntime runtime = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
                using RuntimeAuthoritativeSessionHost host = new(runtime);
                host.JoinSession(SessionId, SessionId);
                runtime.Enemies.Clear();
                Vector3 start = Position(runtime);
                for (int tick = 1; tick <= ProbeTicks; tick++)
                {
                    host.AdvanceTick(
                        (ulong)tick,
                        SimulationClock.FixedDeltaSeconds,
                        [Walk(new PlayerSessionId(SessionId), (ulong)tick, direction)],
                        PlayerSimulationTuning.AuthoritativeDefault,
                        allowGameplaySimulation: false);
                }

                return (Position(runtime) - start).Xz.Length;
            })
            .First();

        static Vector3 Position(WorldSessionRuntime runtime) =>
            Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == SessionId).Position;
    }

    private static PlayerCommandFrame Walk(PlayerSessionId session, ulong sequence, Vector3 facing)
    {
        return new PlayerCommandFrame(
            session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private sealed record StallReport(
        float MaxCorrectionBefore,
        float MaxCorrectionAfter,
        float MaxSmoothAfter,
        int SnapsAfter,
        ulong StallEndTick,
        ulong LastAcknowledged,
        long DroppedTicks,
        long MergedCommands,
        float ServerWalked,
        IReadOnlyList<ulong> SentSequences)
    {
        public string Describe(string label) =>
            $"{label}: max correction before {MaxCorrectionBefore:F3} m, after {MaxCorrectionAfter:F3} m " +
            $"(largest smooth {MaxSmoothAfter:F3} m, snaps {SnapsAfter}); stall end tick {StallEndTick}, last ack {LastAcknowledged}, " +
            $"dropped ticks {DroppedTicks}, merged commands {MergedCommands}, commands sent {SentSequences.Count}, server walked {ServerWalked:F2} m";
    }

    // PlayScene's per-tick client loop without rendering.
    private sealed class HeadlessClient : IDisposable
    {
        private readonly RemoteClientSession _session;
        private readonly ClientWorldSession _world;
        private readonly ClientPredictionReconciler _reconciler = new();
        private readonly PlayerSessionId _remoteSessionId;
        private readonly Vector3 _facing;
        private float _maxCorrectionBefore;
        private float _maxCorrectionAfter;
        private float _maxSmoothAfter;
        private int _snapsAfter;
        private ulong _lastAcknowledged;
        private Vector3? _firstServerPosition;
        private Vector3 _lastServerPosition;

        public HeadlessClient(RemoteClientSession session, int floorSize, Vector3 facing)
        {
            _session = session;
            _remoteSessionId = new PlayerSessionId(session.SessionId);
            _facing = facing;
            _world = new ClientWorldSession(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            Ticks = new ClientCommandTickStream(_world.LocalPlayerSessionId);
            // As PlayScene wires it: a send stalled past the server's repeat window makes the
            // prediction ticks idle.
            Ticks.ObserveSendBacklog(() => _session.PendingCommandCount);
        }

        public ClientCommandTickStream Ticks { get; }

        public bool SelfSeen { get; private set; }

        public ulong StallStartTick { get; set; } = ulong.MaxValue;

        public ulong StallEndTick { get; set; } = ulong.MaxValue;

        public void Dispose()
        {
            _world.Dispose();
        }

        public void Frame(double elapsedSeconds)
        {
            _world.Player.NudgeWorldPosition(_reconciler.TakeSmoothingStep((float)elapsedSeconds));
            Ticks.CaptureInput(Walk(_world.LocalPlayerSessionId, 0, _facing));
            Ticks.Advance(elapsedSeconds, this, static (client, command, fixedDeltaSeconds) => client.Tick(command, fixedDeltaSeconds));
        }

        private void Tick(PlayerCommandFrame command, float fixedDeltaSeconds)
        {
            _session.QueueCommandFrame(RemotePlayerCommandRelay.CreateRelayCommand(command, _remoteSessionId, _world.Player.Camera.Front));
            _world.AdvancePredictionTick(
                command.Sequence,
                fixedDeltaSeconds,
                ClientSimulationCommandPolicy.PrepareForLocalSimulation(command, useRemoteAuthority: true),
                PlayerSimulationTuning.AuthoritativeDefault,
                primaryUseHeld: command.PrimaryUsePressed);
            _reconciler.RecordPrediction(command.Sequence, _world.Player.GetWorldPosition());
        }

        public void Drain()
        {
            while (_session.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
            {
                if (message is JoinAcceptedMessage accepted)
                {
                    ClientJoinAcceptedApplier.ApplyToRuntime(_world, accepted);
                    _reconciler.Reset();
                    continue;
                }

                if (message is not ServerSnapshotMessage snapshotMessage)
                {
                    continue;
                }

                ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                Vector3 before = _world.Player.GetWorldPosition();
                Vector3 pendingBefore = _reconciler.PendingOffset;
                RemoteWorldMutationApplier.ApplySnapshotToRuntime(_world, snapshot);
                ClientCorrectionApplier.ApplySnapshotToRuntime(_world, snapshot, _session.SessionId, _reconciler);
                float jump = (_world.Player.GetWorldPosition() - before).Length;
                float smooth = (_reconciler.PendingOffset - pendingBefore).Length;
                foreach (ActorSnapshot actor in snapshot.Actors)
                {
                    if (actor.Kind == CreatureKind.Player && actor.SessionId == _session.SessionId)
                    {
                        SelfSeen = true;
                        _firstServerPosition ??= actor.Position;
                        _lastServerPosition = actor.Position;
                    }
                }

                if (!snapshot.LastAcceptedCommandSequences.TryGetValue(_session.SessionId, out ulong acknowledged))
                {
                    continue;
                }

                _lastAcknowledged = Math.Max(_lastAcknowledged, acknowledged);
                float correction = Math.Max(jump, smooth);
                if (acknowledged < StallStartTick)
                {
                    _maxCorrectionBefore = Math.Max(_maxCorrectionBefore, correction);
                }
                else
                {
                    _maxCorrectionAfter = Math.Max(_maxCorrectionAfter, correction);
                    _maxSmoothAfter = Math.Max(_maxSmoothAfter, smooth);
                    if (jump > 0.01f)
                    {
                        _snapsAfter++;
                    }
                }
            }
        }

        public StallReport CreateReport(IReadOnlyList<ulong> sentSequences, long mergedCommands)
        {
            return new StallReport(
                _maxCorrectionBefore,
                _maxCorrectionAfter,
                _maxSmoothAfter,
                _snapsAfter,
                StallEndTick,
                _lastAcknowledged,
                Ticks.DroppedTicks,
                mergedCommands,
                _firstServerPosition.HasValue ? (_lastServerPosition - _firstServerPosition.Value).Xz.Length : 0f,
                sentSequences);
        }
    }

    private sealed class StallableTransport : IClientTransport
    {
        public StallableConnection? Connection { get; private set; }

        public async ValueTask<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            ITransportConnection inner = await new TcpClientConnector().ConnectAsync(host, port, cancellationToken);
            Connection = new StallableConnection(inner);
            return Connection;
        }
    }

    // Blocks sends while stalled and records the command sequences that reach the wire.
    private sealed class StallableConnection(ITransportConnection inner) : ITransportConnection
    {
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private readonly List<ulong> _sentCommands = [];
        private volatile TaskCompletionSource? _gate;

        public string ConnectionId => inner.ConnectionId;

        public void Stall()
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Resume()
        {
            _gate?.TrySetResult();
        }

        public IReadOnlyList<ulong> SentCommandSequences()
        {
            lock (_sentCommands)
            {
                return _sentCommands.ToArray();
            }
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? gate = _gate;
            if (gate != null)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            // An empty frame is the session's keepalive; it carries no message.
            if (_framer.TryDeframe(payload.Span, out byte[]? message, out _) &&
                message!.Length > 0 &&
                _codec.Decode(message).Message is PlayerCommandMessage command)
            {
                lock (_sentCommands)
                {
                    _sentCommands.Add(command.CommandFrame.Sequence);
                }
            }

            await inner.SendAsync(payload, cancellationToken);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReceiveAsync(buffer, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Resume();
            return inner.DisposeAsync();
        }
    }
}
