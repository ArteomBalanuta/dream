using System.Buffers;
using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Multiplayer.Protocol;

// SCALE-2: delta-coded actor frames. The server diffs each broadcast's actors against the previous
// broadcast (ActorDeltaChain), the wire carries change-masked records (ActorFrameJson), and the
// client's ActorStateCache rebuilds the full actors. These tests drive scripted 8- and 32-player
// sessions through the real codec, SharedEncodedPayload and ServerConnection.
public sealed class DeltaActorSnapshotTests(ITestOutputHelper output)
{
    private const int SnapshotIntervalTicks = 2;
    private const int SessionTicks = 1000;
    private static readonly TimeSpan FlushDeadline = TimeSpan.FromSeconds(30);

    // Over 1000 ticks of a scripted session (walking, idling, turning, damage, deaths and respawns,
    // mode switches, level-ups, starved ticks, a leave and a rejoin, a rename), every snapshot's
    // rebuilt actors equal the server's within quantization; the presenter interpolates them to
    // the same poses and the reconciler reaches the same decisions.
    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    public void ScriptedSession_ClientRebuildsServerActors_WithinQuantization_ForEveryTick(int players)
    {
        ScriptedSession session = new(players, enemies: 2 + players / 4, seed: 4_000 + players);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        ClientScenePresenter serverView = new(ScriptedSession.SessionId(0));
        ClientScenePresenter clientView = new(ScriptedSession.SessionId(0));
        ClientPredictionReconciler serverReconciler = new();
        ClientPredictionReconciler clientReconciler = new();
        long deltaBytes = 0;
        long legacyBytes = 0;
        int frames = 0;
        int keyframes = 0;
        int comparedPoses = 0;
        int reconciliations = 0;
        float maxPoseError = 0f;

        for (ulong tick = SnapshotIntervalTicks; tick <= SessionTicks; tick += SnapshotIntervalTicks)
        {
            ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
            ServerSnapshotMessage sent = new(Envelope(tick, session.AcceptedSequences(tick), actors) with { ActorDelta = chain.Next(tick, actors) });
            keyframes += sent.Snapshot.ActorDelta!.Value.IsKeyframe ? 1 : 0;

            byte[] frame = EncodeShared(codec, sent, ScriptedSession.SessionId(0), (ulong)frames + 1);
            deltaBytes += frame.Length;
            legacyBytes += codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, sent.MessageKind, (ulong)frames + 1), new ServerSnapshotMessage(sent.Snapshot with { ActorDelta = null })).Length;
            frames++;

            ServerSnapshotMessage received = cache.Apply(Assert.IsType<ServerSnapshotMessage>(codec.Decode(frame).Message));
            AssertWithinQuantization(actors, received.Snapshot.Actors, $"tick {tick}");
            Assert.Equal(tick, Assert.Single(received.Snapshot.LastAcceptedCommandSequences, entry => entry.Key == ScriptedSession.SessionId(0)).Value);

            // Interpolation: the same poses from the server's actors and the rebuilt ones.
            serverView.ApplySnapshot(sent.Snapshot);
            clientView.ApplySnapshot(received.Snapshot);
            double renderTick = tick - 2.5d;
            Dictionary<int, InterpolatedActorPose> serverPoses = serverView.BuildRemotePlayerStates(renderTick).ToDictionary(state => state.ActorId, state => state.Pose);
            foreach (RemotePlayerPresentationState state in clientView.BuildRemotePlayerStates(renderTick))
            {
                InterpolatedActorPose expected = serverPoses[state.ActorId];
                float error = MathF.Max((state.Pose.Position - expected.Position).Length, (state.Pose.Facing - expected.Facing).Length);
                maxPoseError = MathF.Max(maxPoseError, error);
                comparedPoses++;
            }

            Assert.Equal(serverPoses.Count, clientView.BuildRemotePlayerStates(renderTick).Count);

            // Reconciliation of the local player: a prediction off by a scripted amount (inside the
            // deadband, a smoothing, a snap) gets the same decision from either position.
            ActorSnapshot local = actors.First(actor => actor.SessionId == ScriptedSession.SessionId(0));
            ActorSnapshot rebuiltLocal = received.Snapshot.Actors.First(actor => actor.SessionId == ScriptedSession.SessionId(0));
            Vector3 offset = (tick / SnapshotIntervalTicks % 3) switch
            {
                0 => new Vector3(0.01f, 0f, 0f),
                1 => new Vector3(0.4f, 0f, 0.2f),
                _ => new Vector3(4f, 0f, 0f)
            };
            serverReconciler.RecordPrediction(tick, local.Position - offset);
            clientReconciler.RecordPrediction(tick, local.Position - offset);
            ClientReconciliationResult fromServer = serverReconciler.Reconcile(tick, local.Position, starvedTicks: local.StarvedTicks);
            ClientReconciliationResult fromClient = clientReconciler.Reconcile(tick, rebuiltLocal.Position, starvedTicks: rebuiltLocal.StarvedTicks);
            Assert.Equal(fromServer.Kind, fromClient.Kind);
            // The positions differ by at most one quantization error, and so can the pending
            // smoothing offset the prediction was recorded with.
            Assert.True((fromServer.Error - fromClient.Error).Length <= 2.2f * ActorQuantization.MaxError * MathF.Sqrt(3f));
            reconciliations++;
        }

        Assert.Equal(0, cache.ChainBreaks);
        Assert.Equal(0, cache.StaleActorsWithheld);
        Assert.True(keyframes >= SessionTicks / (int)ActorDeltaContext.KeyframeIntervalTicks, $"{keyframes} keyframes");
        Assert.True(comparedPoses > 0);
        Assert.True(maxPoseError <= 2f * ActorQuantization.MaxError * MathF.Sqrt(3f), $"pose error {maxPoseError}");
        Assert.True(session.Renamed && session.Rejoined && session.EnemyRespawns > 0, "The script did not cover what it claims to.");
        // Deltas pay off even though most scripted players keep walking and turning.
        Assert.True(deltaBytes * 3 < legacyBytes, $"delta {deltaBytes} B against legacy {legacyBytes} B");
        output.WriteLine(
            $"{players} players: {frames} snapshots ({keyframes} keyframes), {deltaBytes / frames} B per delta frame against " +
            $"{legacyBytes / frames} B full ({100d * deltaBytes / legacyBytes:0.0}%), {comparedPoses} poses (max error {maxPoseError:E2}), " +
            $"{reconciliations} reconciliations");
    }

    // Lost frames (the client misses some): the cache notices the chain break, shows no actor whose
    // state it cannot vouch for, and has every actor back, exact, by the next keyframe.
    [Fact]
    public void DroppedFrames_BreakTheChain_WithholdStaleActors_AndRecoverByTheNextKeyframe()
    {
        ScriptedSession session = new(players: 32, enemies: 10, seed: 77);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        HashSet<ulong> dropped = [100, 102, 236, 400, 402, 404];
        ulong? recoveredAt = null;
        int withheldSnapshots = 0;
        int frames = 0;

        for (ulong tick = SnapshotIntervalTicks; tick <= 600; tick += SnapshotIntervalTicks)
        {
            ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
            ServerSnapshotMessage sent = new(Envelope(tick, session.AcceptedSequences(tick), actors) with { ActorDelta = chain.Next(tick, actors) });
            byte[] frame = EncodeShared(codec, sent, ScriptedSession.SessionId(0), (ulong)++frames);
            if (dropped.Contains(tick))
            {
                continue;
            }

            long breaksBefore = cache.ChainBreaks;
            IReadOnlyList<ActorSnapshot> shown = cache.Apply(Assert.IsType<ServerSnapshotMessage>(codec.Decode(frame).Message)).Snapshot.Actors;

            // Whatever is shown is the server's current state, never an older one.
            Dictionary<int, ActorSnapshot> server = actors.ToDictionary(actor => actor.ActorId);
            foreach (ActorSnapshot actor in shown)
            {
                AssertWithinQuantization(server[actor.ActorId], actor, $"tick {tick}");
            }

            if (dropped.Contains(tick - SnapshotIntervalTicks) && !sent.Snapshot.ActorDelta!.Value.IsKeyframe)
            {
                Assert.Equal(breaksBefore + 1, cache.ChainBreaks);
            }

            if (shown.Count < actors.Length)
            {
                withheldSnapshots++;
                Assert.True(cache.StaleActorCount > 0);
                recoveredAt = null;
            }
            else
            {
                Assert.Equal(0, cache.StaleActorCount);
                recoveredAt ??= tick;
            }
        }

        Assert.True(cache.ChainBreaks >= 3, $"{cache.ChainBreaks} chain breaks");
        Assert.True(withheldSnapshots > 0);
        Assert.True(cache.StaleActorsWithheld > 0);
        // Recovered, and within one keyframe interval of the last drop.
        Assert.NotNull(recoveredAt);
        Assert.True(recoveredAt <= 404 + ActorDeltaContext.KeyframeIntervalTicks, $"recovered at {recoveredAt}");
        output.WriteLine($"{cache.ChainBreaks} chain breaks, {withheldSnapshots} snapshots withheld actors ({cache.StaleActorsWithheld} actor-snapshots), recovered at tick {recoveredAt}");
    }

    // A connection that misses broadcasts because its writer fell behind (a slow link) gets them
    // merged. The merged snapshot's own base was never sent to it, so the connection re-diffs the
    // frame against the frame its client does hold: a delta, not a keyframe. Only the chain's own
    // keyframes go out whole, nothing is forced, and the client's chain never breaks.
    [Fact]
    public async Task CoalescedSnapshots_AreDeltasAgainstTheFrameTheClientHolds_AndTheChainNeverBreaks()
    {
        ScriptedSession session = new(players: 8, enemies: 4, seed: 9);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport transport = new("gated");
        await using ServerConnection connection = new(transport, host, codec, framer, WithoutLiveness);
        string sessionId = ScriptedSession.SessionId(1);
        host.HandleClientMessage(transport.ConnectionId, new ClientJoinRequestMessage(sessionId, "gated"));
        HashSet<ulong> chainKeyframes = [];
        Dictionary<ulong, ActorSnapshot[]> serverActors = [];
        ActorSnapshot[] last = [];
        for (ulong tick = SnapshotIntervalTicks; tick <= 400; tick += SnapshotIntervalTicks)
        {
            // A slow link: the writer stalls for 10 of every 16 ticks, so several broadcasts merge
            // each time.
            if (tick % 16 == 0)
            {
                transport.Close();
            }

            ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
            last = actors;
            ActorDeltaContext delta = chain.Next(tick, actors);
            if (delta.IsKeyframe)
            {
                chainKeyframes.Add(delta.Sequence);
            }

            serverActors[delta.Sequence] = actors;
            ServerSnapshotMessage message = new(Envelope(tick, session.AcceptedSequences(tick), actors) with { ActorDelta = delta });
            connection.EnqueueSnapshot(message, new SharedEncodedPayload(codec, message));
            // The first snapshot goes out alone: merged with the next before anything was sent,
            // the connection would hold no frame to diff against and send a keyframe (a join).
            if (tick % 16 == 10 || tick == SnapshotIntervalTicks)
            {
                transport.Open();
                Assert.True(await connection.FlushOutboundAsync(FlushDeadline));
            }
        }

        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(FlushDeadline));
        Assert.True(connection.CoalescedSnapshots >= 20, $"{connection.CoalescedSnapshots} merged");
        Assert.Equal(0, connection.ActorKeyframesForced);
        Assert.True(connection.ActorDeltasRebased >= 20, $"{connection.ActorDeltasRebased} rebased deltas");

        ActorStateCache cache = new();
        long sentBytes = 0;
        long keyframeBytes = 0;
        int rebased = 0;
        ulong previous = 0;
        byte[][] frames = transport.Frames;
        ServerSnapshotMessage[] received = [.. DecodeFrames(codec, framer, frames).Select(cache.Apply)];
        for (int i = 0; i < received.Length; i++)
        {
            ServerSnapshotEnvelope snapshot = received[i].Snapshot;
            ActorWireFrame frame = snapshot.ActorFrame!;
            if (frame.IsKeyframe)
            {
                Assert.Contains(frame.Sequence, chainKeyframes);
            }
            else
            {
                Assert.Equal(previous, frame.BaseSequence);
            }

            if (!frame.IsKeyframe && frame.BaseSequence != frame.Sequence - 1)
            {
                // What the same merged snapshot cost as a keyframe (before the rebase).
                rebased++;
                sentBytes += frames[i].Length - framer.HeaderLength;
                ServerSnapshotMessage asKeyframe = new(snapshot with { ActorFrame = null, ActorDelta = ActorDeltaContext.CreateKeyframe(frame.Sequence) });
                keyframeBytes += codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, asKeyframe.MessageKind, (ulong)i + 1), asKeyframe).Length;
            }

            AssertWithinQuantization(serverActors[frame.Sequence], snapshot.Actors, $"frame {frame.Sequence}");
            Assert.Equal(snapshot.ServerTick, snapshot.LastAcceptedCommandSequences[sessionId]);
            previous = frame.Sequence;
        }

        output.WriteLine(
            $"{received.Length} snapshots, {connection.CoalescedSnapshots} merged, {rebased} rebased deltas: {sentBytes} B against {keyframeBytes} B as keyframes " +
            $"({100d * sentBytes / keyframeBytes:0.0}%); total sent {connection.BytesSent} B");
        Assert.Equal(connection.ActorDeltasRebased, rebased);
        Assert.True(sentBytes * 2 < keyframeBytes, $"rebased deltas {sentBytes} B, as keyframes {keyframeBytes} B");
        Assert.Equal(0, cache.ChainBreaks);
        Assert.Equal(0, cache.StaleActorsWithheld);
        AssertWithinQuantization(last, received[^1].Snapshot.Actors, "last snapshot");
    }

    // A shared broadcast past the peer's frame cap goes out without its deltas, encoded for each
    // connection alone. Its actors keep the frame and base the shared payload had: a connection
    // that holds the base keeps its chain (a delta), one that just joined gets the keyframe, which
    // is counted once. The stripped body carries the full accepted-sequence map, which the client
    // decodes.
    [Fact]
    public async Task OversizedSharedDelta_StrippedFallback_KeepsTheActorChain_AndTheBodyAck()
    {
        ScriptedSession session = new(players: 4, enemies: 1, seed: 33);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new(maxReadPayloadBytes: 64 * 1024, maxWritePayloadBytes: 8 * 1024);
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport earlyTransport = new("early");
        GatedTransport lateTransport = new("late");
        await using ServerConnection early = new(earlyTransport, host, codec, framer, WithoutLiveness);
        await using ServerConnection late = new(lateTransport, host, codec, framer, WithoutLiveness);
        string earlySession = ScriptedSession.SessionId(1);
        string lateSession = ScriptedSession.SessionId(2);
        host.HandleClientMessage(earlyTransport.ConnectionId, new ClientJoinRequestMessage(earlySession, "early"));
        host.HandleClientMessage(lateTransport.ConnectionId, new ClientJoinRequestMessage(lateSession, "late"));
        const ulong OversizedTick = 12;
        ulong oversizedSequence = 0;
        ActorSnapshot[] last = [];
        for (ulong tick = SnapshotIntervalTicks; tick <= 30; tick += SnapshotIntervalTicks)
        {
            ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
            last = actors;
            ServerSnapshotEnvelope envelope = Envelope(tick, session.AcceptedSequences(tick), actors) with { ActorDelta = chain.Next(tick, actors) };
            if (tick == OversizedTick)
            {
                // About 18 KB of mutations: past the 8 KB cap.
                envelope = envelope with { WorldMutations = [.. Enumerable.Range(1, 300).Select(i => Mutation((ulong)i))] };
                Assert.False(envelope.ActorDelta!.Value.IsKeyframe);
                oversizedSequence = envelope.ActorDelta.Value.Sequence;
            }

            ServerSnapshotMessage message = new(envelope);
            SharedEncodedPayload shared = new(codec, message);
            early.EnqueueSnapshot(message, shared);
            if (tick >= OversizedTick)
            {
                late.EnqueueSnapshot(message, shared);
            }

            Assert.True(await early.FlushOutboundAsync(FlushDeadline));
            Assert.True(await late.FlushOutboundAsync(FlushDeadline));
        }

        Assert.Equal(1, early.OversizedSnapshotFrames);
        Assert.Equal(1, late.OversizedSnapshotFrames);
        Assert.Equal(0, early.ActorKeyframesForced);
        // The join keyframe, counted once although the frame was encoded twice.
        Assert.Equal(1, late.ActorKeyframesForced);

        foreach ((GatedTransport transport, string sessionId) in new[] { (earlyTransport, earlySession), (lateTransport, lateSession) })
        {
            ActorStateCache cache = new();
            ServerSnapshotMessage[] received = [.. DecodeFrames(codec, framer, transport.Frames).Select(cache.Apply)];
            Assert.All(transport.Frames, frame => Assert.True(frame.Length - framer.HeaderLength <= 8 * 1024, $"a {frame.Length} B frame"));
            ServerSnapshotEnvelope stripped = Assert.Single(received, message => message.Snapshot.ServerTick == OversizedTick).Snapshot;
            Assert.Empty(stripped.WorldMutations);
            Assert.Equal(oversizedSequence, stripped.ActorFrame!.Sequence);
            if (transport == earlyTransport)
            {
                Assert.False(stripped.ActorFrame.IsKeyframe);
                Assert.Equal(oversizedSequence - 1, stripped.ActorFrame.BaseSequence);
            }
            else
            {
                Assert.True(stripped.ActorFrame.IsKeyframe);
                Assert.Same(received[0].Snapshot.ActorFrame, stripped.ActorFrame);
            }

            // The stripped body has the full map; every other snapshot this connection's own ack.
            Assert.Equal(session.AcceptedSequences(OversizedTick).Count, stripped.LastAcceptedCommandSequences.Count);
            Assert.All(received, message => Assert.Equal(message.Snapshot.ServerTick, message.Snapshot.LastAcceptedCommandSequences[sessionId]));
            Assert.Equal(0, cache.ChainBreaks);
            Assert.Equal(0, cache.StaleActorsWithheld);
            AssertWithinQuantization(last, received[^1].Snapshot.Actors, transport.ConnectionId);
        }
    }

    // A world resync between two delta frames: the resync snapshot is off the chain (full actor
    // list, no frame), so the next broadcast's delta, whose base the client dropped with the
    // resync, goes out as its keyframe twin, and deltas resume after it.
    [Fact]
    public async Task ResyncBetweenTwoDeltas_IsFollowedByTheKeyframeTwin_AndDeltasResume()
    {
        ScriptedSession session = new(players: 4, enemies: 2, seed: 44);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport transport = new("resynced");
        await using ServerConnection connection = new(transport, host, codec, framer, WithoutLiveness);
        string sessionId = ScriptedSession.SessionId(1);
        host.HandleClientMessage(transport.ConnectionId, new ClientJoinRequestMessage(sessionId, "resynced"));
        const ulong ResyncTick = 13;
        ulong twinSequence = 0;
        ActorSnapshot[] last = [];
        for (ulong tick = SnapshotIntervalTicks; tick <= 24; tick += SnapshotIntervalTicks)
        {
            ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
            last = actors;
            ServerSnapshotMessage message = new(Envelope(tick, session.AcceptedSequences(tick), actors) with { ActorDelta = chain.Next(tick, actors) });
            if (tick == ResyncTick + 1)
            {
                Assert.False(message.Snapshot.ActorDelta!.Value.IsKeyframe);
                twinSequence = message.Snapshot.ActorDelta.Value.Sequence;
            }

            connection.EnqueueSnapshot(message, new SharedEncodedPayload(codec, message));
            Assert.True(await connection.FlushOutboundAsync(FlushDeadline));
            if (tick == ResyncTick - 1)
            {
                // Between this delta and the next: a resync snapshot, encoded for this connection
                // alone and off the chain (the runtime never runs it through ActorDeltaChain).
                connection.EnqueueSnapshot(new ServerSnapshotMessage(Envelope(ResyncTick, session.AcceptedSequences(ResyncTick), actors)));
                Assert.True(await connection.FlushOutboundAsync(FlushDeadline));
            }
        }

        Assert.Equal(1, connection.ActorKeyframesForced);
        Assert.Equal(0, connection.ActorDeltasRebased);

        ActorStateCache cache = new();
        ServerSnapshotMessage[] received = [.. DecodeFrames(codec, framer, transport.Frames).Select(cache.Apply)];
        int resync = Array.FindIndex(received, message => message.Snapshot.ServerTick == ResyncTick);
        Assert.True(resync > 1);
        Assert.False(received[resync - 1].Snapshot.ActorFrame!.IsKeyframe);
        Assert.Null(received[resync].Snapshot.ActorFrame);
        ActorWireFrame twin = received[resync + 1].Snapshot.ActorFrame!;
        Assert.True(twin.IsKeyframe);
        Assert.Equal(twinSequence, twin.Sequence);
        ActorWireFrame next = received[resync + 2].Snapshot.ActorFrame!;
        Assert.False(next.IsKeyframe);
        Assert.Equal(twinSequence, next.BaseSequence);
        // The resync body carries the full map; the broadcasts this connection's ack in the header.
        Assert.Equal(session.AcceptedSequences(ResyncTick).Count, received[resync].Snapshot.LastAcceptedCommandSequences.Count);
        Assert.All(received, message => Assert.Equal(message.Snapshot.ServerTick, message.Snapshot.LastAcceptedCommandSequences[sessionId]));
        Assert.Single(received[resync + 1].Snapshot.LastAcceptedCommandSequences);
        Assert.Equal(0, cache.ChainBreaks);
        Assert.Equal(0, cache.StaleActorsWithheld);
        AssertWithinQuantization(last, received[^1].Snapshot.Actors, "last snapshot");
    }

    // A connection that joins between keyframes receives its first broadcast (a delta against a
    // frame it never got) as the keyframe twin of the shared payload, encoded once for every such
    // connection; a connection that holds the base gets the delta bytes of the same payload.
    [Fact]
    public async Task JoinBetweenKeyframes_GetsTheKeyframeTwin_WhileOthersGetTheDelta()
    {
        ScriptedSession session = new(players: 8, enemies: 2, seed: 21);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport earlyTransport = new("early");
        GatedTransport lateTransport = new("late");
        await using ServerConnection early = new(earlyTransport, host, codec, framer, WithoutLiveness);
        await using ServerConnection late = new(lateTransport, host, codec, framer, WithoutLiveness);
        SharedEncodedPayload? joinPayload = null;
        ulong joinTick = 40;
        Assert.NotEqual(0ul, joinTick % ActorDeltaContext.KeyframeIntervalTicks);
        ActorSnapshot[] last = [];
        for (ulong tick = SnapshotIntervalTicks; tick <= 80; tick += SnapshotIntervalTicks)
        {
            ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
            last = actors;
            ServerSnapshotMessage message = new(Envelope(tick, session.AcceptedSequences(tick), actors) with { ActorDelta = chain.Next(tick, actors) });
            SharedEncodedPayload shared = new(codec, message);
            early.EnqueueSnapshot(message, shared);
            if (tick >= joinTick)
            {
                if (tick == joinTick)
                {
                    Assert.False(message.Snapshot.ActorDelta!.Value.IsKeyframe);
                    joinPayload = shared;
                }

                late.EnqueueSnapshot(message, shared);
            }

            Assert.True(await early.FlushOutboundAsync(FlushDeadline));
            Assert.True(await late.FlushOutboundAsync(FlushDeadline));
        }

        Assert.Equal(0, early.ActorKeyframesForced);
        Assert.Equal(1, late.ActorKeyframesForced);
        Assert.True(joinPayload!.IsEncoded);
        Assert.True(joinPayload.IsKeyframeEncoded);
        Assert.True(joinPayload.KeyframeBytes.Length > joinPayload.Bytes.Length);

        foreach (GatedTransport transport in new[] { earlyTransport, lateTransport })
        {
            ActorStateCache cache = new();
            ServerSnapshotMessage[] received = [.. DecodeFrames(codec, framer, transport.Frames).Select(cache.Apply)];
            Assert.Equal(0, cache.ChainBreaks);
            Assert.Equal(0, cache.StaleActorsWithheld);
            AssertWithinQuantization(last, received[^1].Snapshot.Actors, transport.ConnectionId);
            if (transport == lateTransport)
            {
                Assert.True(received[0].Snapshot.ActorFrame!.IsKeyframe);
                Assert.Equal(joinTick, received[0].Snapshot.ServerTick);
            }
        }
    }

    // A client-side join between keyframes that somehow got a delta first (a server bug, or a
    // replayed stream) shows nothing for the actors it cannot rebuild, and everything once the next
    // keyframe arrives.
    [Fact]
    public void CacheThatStartsOnADelta_ShowsOnlyFullRecords_UntilTheKeyframe()
    {
        ScriptedSession session = new(players: 4, enemies: 0, seed: 5);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        int shownEmpty = 0;
        for (ulong tick = SnapshotIntervalTicks; tick <= 2 * ActorDeltaContext.KeyframeIntervalTicks; tick += SnapshotIntervalTicks)
        {
            ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
            ServerSnapshotMessage sent = new(Envelope(tick, session.AcceptedSequences(tick), actors) with { ActorDelta = chain.Next(tick, actors) });
            if (tick < 10)
            {
                continue;
            }

            byte[] frame = EncodeShared(codec, sent, null, tick);
            IReadOnlyList<ActorSnapshot> shown = cache.Apply(Assert.IsType<ServerSnapshotMessage>(codec.Decode(frame).Message)).Snapshot.Actors;
            if (sent.Snapshot.ActorDelta!.Value.IsKeyframe)
            {
                AssertWithinQuantization(actors, shown, $"keyframe at {tick}");
                break;
            }

            Assert.Empty(shown);
            shownEmpty++;
        }

        Assert.True(shownEmpty > 0);
        Assert.Equal(0, cache.StaleActorCount);
    }

    // Golden bytes: an idle actor is its bare id; a walking one names the group and the change.
    [Fact]
    public void IdleActor_EncodesToItsId_AndAMovingActorToAFewBytes()
    {
        ActorSnapshot idle = Player(17, "session-17", new Vector3(10f, 2f, 30f));
        ActorSnapshot walker = Player(18, "session-18", new Vector3(12.5f, 2f, 30f));
        ActorSnapshot enemy = new(2001, CreatureKind.Enemy, null, new Vector3(5f, 1f, 5f), Vector3.UnitX, 40, 40, true, DisplayName: "Wolf", EnemyBehaviorState: EnemyBehaviorState.Idle);
        ActorDeltaChain chain = new();
        ActorSnapshot[] first = [idle, walker, enemy];
        ActorDeltaContext keyframe = chain.Next(2, first);
        Assert.True(keyframe.IsKeyframe);

        ActorSnapshot[] second = [idle, walker with { Position = walker.Position + new Vector3(0.1f, 0f, -0.05f) }, enemy];
        ServerSnapshotMessage delta = new(Envelope(4, new Dictionary<string, ulong>(), second) with { ActorDelta = chain.Next(4, second) }) { SharedBody = true };
        string json = Encoding.UTF8.GetString(EncodePayload(new TransportJsonCodec(), delta));

        // q(12.6) - q(12.5) = 51610 - 51200 = 410; q(29.95) - q(30) = 122675 - 122880 = -205.
        Assert.Contains("\"actorFrame\":{\"seq\":2,\"base\":1,\"actors\":[17,[18,2,410,0,-205],2001]}", json, StringComparison.Ordinal);
        Assert.Equal(2, "17".Length);
        output.WriteLine(json);

        // The keyframe of the same frame carries every actor whole.
        ServerSnapshotMessage key = new(Envelope(2, new Dictionary<string, ulong>(), first) with { ActorDelta = keyframe }) { SharedBody = true };
        string keyJson = Encoding.UTF8.GetString(EncodePayload(new TransportJsonCodec(), key));
        Assert.Contains("[17,255,0,\"session-17\",\"name-17\",40960,8192,122880,0,0,-4096,100,100,1,0,null,[10,2,10,0,0],[1,0,1000],0]", keyJson, StringComparison.Ordinal);
        output.WriteLine(keyJson);
    }

    private static ActorSnapshot Enemy(int id, int level = 0, string? visualKey = null, Vector3? position = null) =>
        new(id, CreatureKind.Enemy, null, position ?? new Vector3(5f, 1f, 5f), Vector3.UnitX, 100, 100, true,
            DisplayName: level == 3 ? "Dire Wolf" : "Wolf", EnemyBehaviorState: EnemyBehaviorState.Idle, EnemyLevel: level, VisualKey: visualKey);

    // Wave I (protocol v11): a wolf is mask 255 with no new bytes; a Dire Wolf is 767 with level and key.
    [Fact]
    public void EnemyPresentation_IsOmittedForAWolf_AndCarriedForADire()
    {
        ActorSnapshot wolf = Enemy(2001);
        ActorSnapshot dire = Enemy(2002, 3, "enemy.dire-wolf");
        ActorSnapshot lit = Player(17, "session-17", new Vector3(10f, 2f, 30f)) with { HeldLight = Dream.Simulation.Inventory.HeldLightKind.Torch };

        Assert.Equal(255, (int)ActorWireRecord.Full(wolf).Groups);
        Assert.Equal(511, (int)ActorWireRecord.Full(lit).Groups);
        Assert.Equal(767, (int)ActorWireRecord.Full(dire).Groups);

        ActorSnapshot[] actors = [wolf, dire];
        string key = Encoding.UTF8.GetString(EncodePayload(new TransportJsonCodec(), new ServerSnapshotMessage(
            Envelope(2, new Dictionary<string, ulong>(), actors) with { ActorDelta = new ActorDeltaChain().Next(2, actors) }) { SharedBody = true }));
        output.WriteLine(key);
        Assert.Contains("[2001,255,1,null,\"Wolf\",", key, StringComparison.Ordinal);
        Assert.DoesNotContain("[2001,767", key, StringComparison.Ordinal);
        Assert.Contains("[2002,767,1,null,\"Dire Wolf\",", key, StringComparison.Ordinal);
        Assert.Contains(",3,\"enemy.dire-wolf\"]", key, StringComparison.Ordinal);
    }

    [Fact]
    public void EnemyPresentation_RoundTripsInAKeyframe_AndAsADelta()
    {
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        ActorDeltaChain chain = new();
        ActorSnapshot[] first = [Enemy(2001), Enemy(2002, 3, "enemy.dire-wolf")];
        ActorSnapshot[] second = [Enemy(2001), Enemy(2002, 3, "enemy.dire-wolf", new Vector3(6f, 1f, 5f))];
        // Same identity, new presentation: only the presentation group changes for each.
        ActorSnapshot[] third = [Enemy(2001, 3, "enemy.dire-wolf") with { DisplayName = "Wolf" }, Enemy(2002, 0, null, new Vector3(6f, 1f, 5f)) with { DisplayName = "Dire Wolf" }];

        ServerSnapshotMessage a = new(Envelope(2, new Dictionary<string, ulong>(), first) with { ActorDelta = chain.Next(2, first) });
        ServerSnapshotMessage b = new(Envelope(4, new Dictionary<string, ulong>(), second) with { ActorDelta = chain.Next(4, second) });
        ServerSnapshotMessage c = new(Envelope(6, new Dictionary<string, ulong>(), third) with { ActorDelta = chain.Next(6, third) });
        Assert.True(a.Snapshot.ActorDelta!.Value.IsKeyframe);
        Assert.False(b.Snapshot.ActorDelta!.Value.IsKeyframe);

        ServerSnapshotMessage ra = cache.Apply(Assert.IsType<ServerSnapshotMessage>(codec.Decode(EncodeShared(codec, a, null, 1)).Message));
        Assert.Equal((0, null), (ra.Snapshot.Actors[0].EnemyLevel, ra.Snapshot.Actors[0].VisualKey));
        Assert.Equal((3, "enemy.dire-wolf"), (ra.Snapshot.Actors[1].EnemyLevel, ra.Snapshot.Actors[1].VisualKey));

        // An unchanged presentation costs nothing in a delta.
        string bJson = Encoding.UTF8.GetString(EncodePayload(codec, b with { SharedBody = true }));
        Assert.DoesNotContain("enemy.dire-wolf", bJson, StringComparison.Ordinal);
        ServerSnapshotMessage rb = cache.Apply(Assert.IsType<ServerSnapshotMessage>(codec.Decode(EncodeShared(codec, b, null, 2)).Message));
        Assert.Equal((3, "enemy.dire-wolf"), (rb.Snapshot.Actors[1].EnemyLevel, rb.Snapshot.Actors[1].VisualKey));

        string cJson = Encoding.UTF8.GetString(EncodePayload(codec, c with { SharedBody = true }));
        output.WriteLine(cJson);
        Assert.Contains("[2001,512,3,\"enemy.dire-wolf\"]", cJson, StringComparison.Ordinal);
        ServerSnapshotMessage rc = cache.Apply(Assert.IsType<ServerSnapshotMessage>(codec.Decode(EncodeShared(codec, c, null, 3)).Message));
        Assert.Equal((3, "enemy.dire-wolf"), (rc.Snapshot.Actors[0].EnemyLevel, rc.Snapshot.Actors[0].VisualKey));
        Assert.Equal(0, rc.Snapshot.Actors[1].EnemyLevel);
        Assert.Null(rc.Snapshot.Actors[1].VisualKey);
    }

    [Fact]
    public void EnemyPresentation_HostileValues_AreRefusedOrCarriedWithoutThrowing()
    {
        string longKey = new('k', 5000);
        string frame = "{\"seq\":1,\"actors\":[[1,767,1,null,\"x\",0,0,0,0,0,0,1,1,1,null,0,null,null,0,1000000," + System.Text.Json.JsonSerializer.Serialize(longKey) + "]]}";
        string payload = $"{{\"snapshot\":{{\"serverTick\":1,\"actorFrame\":{frame},\"interactionMode\":0}}}}";

        ServerSnapshotMessage message = System.Text.Json.JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, TransportJsonCodecOptions)!;

        ActorWireRecord record = message.Snapshot.ActorFrame!.Records[0];
        Assert.Equal(1_000_000, record.EnemyLevel);
        Assert.Equal(longKey, record.VisualKey);
        string tooHigh = payload.Replace("1000000,", "1000001,");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<ServerSnapshotMessage>(tooHigh, TransportJsonCodecOptions));
    }

    // The legacy actor list (join-accepted, a resync) carries the same two fields, left out at the defaults.
    [Fact]
    public void EnemyPresentation_InTheLegacyActorList_IsLeftOutAtTheDefaults_AndRoundTrips()
    {
        TransportJsonCodec codec = new();
        ActorSnapshot[] actors = [Enemy(2001), Enemy(2002, 3, "enemy.dire-wolf")];
        ServerSnapshotMessage message = new(Envelope(2, new Dictionary<string, ulong>(), actors));

        string json = Encoding.UTF8.GetString(codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));

        Assert.Equal(1, CountOccurrences(json, "\"enemyLevel\""));
        Assert.Equal(1, CountOccurrences(json, "\"visualKey\""));
        ServerSnapshotMessage decoded = Assert.IsType<ServerSnapshotMessage>(codec.Decode(Encoding.UTF8.GetBytes(json)).Message);
        Assert.Equal(0, decoded.Snapshot.Actors[0].EnemyLevel);
        Assert.Null(decoded.Snapshot.Actors[0].VisualKey);
        Assert.Equal((3, "enemy.dire-wolf"), (decoded.Snapshot.Actors[1].EnemyLevel, decoded.Snapshot.Actors[1].VisualKey));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => codec.Decode(Encoding.UTF8.GetBytes(json.Replace("\"enemyLevel\":3", "\"enemyLevel\":2000000"))));
    }

    // The per-connection ack: the shared body carries no session's sequence, and each connection's
    // header carries its own only, so a broadcast to P players writes O(P) acks, not O(P^2).
    [Fact]
    public void SharedBody_HasNoSequenceMap_AndEachHeaderCarriesOneAck()
    {
        ScriptedSession session = new(players: 32, enemies: 0, seed: 3);
        TransportJsonCodec codec = new();
        ActorSnapshot[] actors = session.Step(SnapshotIntervalTicks);
        ServerSnapshotMessage message = new(Envelope(2, session.AcceptedSequences(2), actors) with { ActorDelta = new ActorDeltaChain().Next(2, actors) });
        Assert.Equal(32, message.Snapshot.LastAcceptedCommandSequences.Count);
        SharedEncodedPayload shared = new(codec, message);
        string body = Encoding.UTF8.GetString(shared.Bytes);
        Assert.DoesNotContain("lastAcceptedCommandSequences", body, StringComparison.Ordinal);

        for (int player = 0; player < 32; player++)
        {
            byte[] frame = EncodeShared(codec, message, ScriptedSession.SessionId(player), 1);
            DecodedProtocolMessage decoded = codec.Decode(frame);
            KeyValuePair<string, ulong> ack = Assert.Single(Assert.IsType<ServerSnapshotMessage>(decoded.Message).Snapshot.LastAcceptedCommandSequences);
            Assert.Equal(ScriptedSession.SessionId(player), ack.Key);
            Assert.Equal(message.Snapshot.LastAcceptedCommandSequences[ack.Key], ack.Value);
            // The header adds exactly one session id to the shared body.
            Assert.Equal(1, CountOccurrences(Encoding.UTF8.GetString(frame), "\"session-") - CountOccurrences(body, "\"session-"));
        }
    }

    // Building a chain frame on the tick and encoding it (records built from the context while
    // writing, the keyframe twin included) allocate nothing; decoding and rebuilding stay bounded.
    [Fact]
    public void EncodeIsAllocationFree_AndDecodeWithRebuildIsBounded()
    {
        ScriptedSession session = new(players: 32, enemies: 8, seed: 12);
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorSnapshot[] first = session.Step(SnapshotIntervalTicks);
        ServerSnapshotMessage keyframe = new(Envelope(2, session.AcceptedSequences(2), first) with { ActorDelta = chain.Next(2, first) }) { SharedBody = true };
        Assert.True(keyframe.Snapshot.ActorDelta!.Value.IsKeyframe);
        ActorSnapshot[] second = session.Step(SnapshotIntervalTicks);
        ServerSnapshotMessage delta = new(Envelope(4, session.AcceptedSequences(4), second) with { ActorDelta = chain.Next(4, second) }) { SharedBody = true };
        Assert.False(delta.Snapshot.ActorDelta!.Value.IsKeyframe);
        // The delta frame's keyframe twin, as a connection without the base gets it.
        ServerSnapshotMessage twin = delta with { Snapshot = delta.Snapshot with { ActorDelta = delta.Snapshot.ActorDelta.Value.Keyframe } };

        ActorDeltaChain tickChain = new();
        ulong chainTick = 0;
        long chainNext = AllocationAssert.AtMost(0, () => tickChain.Next(chainTick += SnapshotIntervalTicks, second));

        PooledFrameWriter writer = new();
        long deltaEncode = AllocationAssert.AtMost(0, () =>
        {
            writer.Reset();
            codec.EncodePayload(delta, writer);
        });
        long keyframeEncode = AllocationAssert.AtMost(0, () =>
        {
            writer.Reset();
            codec.EncodePayload(twin, writer);
        });

        // A keyframe and a delta on the client, as they arrive: decode, then rebuild.
        byte[] keyFrame = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, keyframe.MessageKind, 1), keyframe);
        byte[] deltaFrame = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, delta.MessageKind, 2), delta);
        ActorStateCache cache = new();
        long decodeAndRebuild = AllocationAssert.AtMost(DeltaDecodeBudgetBytes, () =>
        {
            cache.Apply((ServerSnapshotMessage)codec.Decode(keyFrame).Message);
            cache.Apply((ServerSnapshotMessage)codec.Decode(deltaFrame).Message);
        });
        output.WriteLine(
            $"40 actors: chain frame {chainNext} B, encode delta {deltaEncode} B, keyframe {keyframeEncode} B; decode + rebuild of a keyframe and a delta {decodeAndRebuild} B " +
            $"(budget {DeltaDecodeBudgetBytes} B); frames {keyFrame.Length} B and {deltaFrame.Length} B");
        Assert.Equal(0, cache.ChainBreaks);
    }

    // Decoding and rebuilding a 40-actor keyframe plus the 40-actor delta after it: two record
    // arrays (about 5 KB each) and two actor arrays (about 4 KB each), the 80 strings of the
    // keyframe's full records, and the messages and envelopes. Measured 22.4 KB (Debug) before wave I;
    // wave I added two fields (an int and a string reference, 16 B with padding) to ActorWireRecord and
    // ActorSnapshot, so those four arrays grew by about 2.6 KB (measured 25.0 KB) and the budget went
    // from 24 KB to 26 KB. Nothing is allocated per actor beyond the arrays. The delta alone (about
    // 9.5 KB) costs about what a 40-actor snapshot cost to decode as the full list.
    private const long DeltaDecodeBudgetBytes = 29 * 1024; // protocol 14 widened the combat group

    // A mangled frame is a JsonException, never a half-built actor.
    [Theory]
    [InlineData("{\"seq\":1,\"actors\":[[1,6,0,0,0,0,0,0]]}")]
    [InlineData("{\"seq\":1,\"actors\":[[1,255,0]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,1,0,null,null]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,512]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,1024,1]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,512,1000001,null]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,512,-1,null]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,512,3,7]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,8,1,1,2]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,2,1,2,3,4]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[\"1\"]}")]
    [InlineData("{\"seq\":0,\"actors\":[]}")]
    public void MangledFrames_FailWithJsonException(string frame)
    {
        string payload = $"{{\"snapshot\":{{\"serverTick\":1,\"actorFrame\":{frame},\"interactionMode\":0}}}}";
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, TransportJsonCodecOptions));
    }

    private static readonly System.Text.Json.JsonSerializerOptions TransportJsonCodecOptions =
        (System.Text.Json.JsonSerializerOptions)typeof(TransportJsonCodec)
            .GetField("SerializerOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static ActorSnapshot Player(int id, string sessionId, Vector3 position)
    {
        return new ActorSnapshot(
            id,
            CreatureKind.Player,
            sessionId,
            position,
            -Vector3.UnitZ,
            100,
            100,
            true,
            PlayerInteractionMode.Building,
            new PlayerCombatStatsSnapshot(10, 2f, 10),
            $"name-{id}",
            new PlayerProgressionSnapshot(1, 0, 1000));
    }

    private static ServerSnapshotEnvelope Envelope(ulong tick, Dictionary<string, ulong> sequences, ActorSnapshot[] actors)
    {
        return new ServerSnapshotEnvelope(
            tick,
            sequences,
            actors,
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    // The frame one connection gets for a broadcast: its own ack in the header, then the shared
    // body, exactly as ServerConnection writes it.
    private static byte[] EncodeShared(TransportJsonCodec codec, ServerSnapshotMessage message, string? sessionId, ulong sequence)
    {
        SharedEncodedPayload shared = new(codec, message);
        TransportAck ack = sessionId != null && message.Snapshot.LastAcceptedCommandSequences.TryGetValue(sessionId, out ulong value)
            ? new TransportAck(sessionId, value)
            : new TransportAck(null, 0);
        ArrayBufferWriter<byte> writer = new();
        codec.WriteEnvelope(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence) { Ack = ack }, shared.Bytes, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] EncodePayload(TransportJsonCodec codec, ServerSnapshotMessage message)
    {
        ArrayBufferWriter<byte> writer = new();
        codec.EncodePayload(message, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static IEnumerable<ServerSnapshotMessage> DecodeFrames(TransportJsonCodec codec, LengthPrefixedMessageFramer framer, byte[][] frames)
    {
        foreach (byte[] frame in frames)
        {
            Assert.True(framer.TryDeframe(frame, out byte[]? payload, out _));
            yield return Assert.IsType<ServerSnapshotMessage>(codec.Decode(payload!).Message);
        }
    }

    private static void AssertWithinQuantization(IReadOnlyList<ActorSnapshot> expected, IReadOnlyList<ActorSnapshot> actual, string where)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            AssertWithinQuantization(expected[i], actual[i], where);
        }
    }

    private static void AssertWithinQuantization(ActorSnapshot expected, ActorSnapshot actual, string where)
    {
        Assert.True(
            Close(expected.Position, actual.Position) && Close(expected.Facing, actual.Facing),
            $"{where}: actor {expected.ActorId} at {actual.Position}/{actual.Facing}, server {expected.Position}/{expected.Facing}");
        Assert.Equal(expected with { Position = default, Facing = default }, actual with { Position = default, Facing = default });
    }

    private static bool Close(Vector3 expected, Vector3 actual)
    {
        // Half a step, plus float rounding of the dequantized value.
        const float Tolerance = ActorQuantization.MaxError * 1.01f;
        return MathF.Abs(expected.X - actual.X) <= Tolerance
            && MathF.Abs(expected.Y - actual.Y) <= Tolerance
            && MathF.Abs(expected.Z - actual.Z) <= Tolerance;
    }

    // A deterministic stand-in for a busy server: players walk, idle, turn, fight, switch modes,
    // level up and starve; enemies chase, die and respawn under new ids; one player leaves and
    // rejoins (a new actor id), one is renamed. Players come first, then enemies, as the runtime
    // orders them.
    private sealed class ScriptedSession
    {
        private const float TickSeconds = 1f / 64f;
        private readonly Random _random;
        private readonly List<ScriptedActor> _players = [];
        private readonly List<ScriptedActor> _enemies = [];
        private int _nextActorId = 1;
        private int _steps;
        private ScriptedActor? _away;

        public ScriptedSession(int players, int enemies, int seed)
        {
            _random = new Random(seed);
            for (int i = 0; i < players; i++)
            {
                _players.Add(NewPlayer(i));
            }

            for (int i = 0; i < enemies; i++)
            {
                _enemies.Add(NewEnemy());
            }
        }

        public bool Renamed { get; private set; }

        public bool Rejoined { get; private set; }

        public int EnemyRespawns { get; private set; }

        public static string SessionId(int index) => $"session-{index:00}";

        public Dictionary<string, ulong> AcceptedSequences(ulong tick)
        {
            Dictionary<string, ulong> sequences = new(StringComparer.Ordinal);
            foreach (ScriptedActor player in _players)
            {
                sequences[player.SessionId!] = tick;
            }

            return sequences;
        }

        public ActorSnapshot[] Step(int ticks)
        {
            _steps++;
            float dt = ticks * TickSeconds;
            foreach (ScriptedActor player in _players)
            {
                StepPlayer(player, dt);
            }

            foreach (ScriptedActor enemy in _enemies)
            {
                StepEnemy(enemy, dt);
            }

            for (int i = 0; i < _enemies.Count; i++)
            {
                if (!_enemies[i].Alive && _random.NextDouble() < 0.1)
                {
                    _enemies[i] = NewEnemy();
                    EnemyRespawns++;
                }
            }

            if (_steps == 120 && _players.Count > 3)
            {
                _away = _players[3];
                _players.RemoveAt(3);
            }
            else if (_steps == 150 && _away != null)
            {
                _players.Insert(3, NewPlayer(3));
                Rejoined = true;
            }
            else if (_steps == 200 && _players.Count > 1)
            {
                _players[1].Name += "-renamed";
                Renamed = true;
            }

            return [.. _players.Select(player => player.ToSnapshot()), .. _enemies.Select(enemy => enemy.ToSnapshot())];
        }

        private void StepPlayer(ScriptedActor player, float dt)
        {
            if (_random.NextDouble() < 0.05)
            {
                // A third of the players idle at any time; the rest walk and look around.
                player.Walking = _random.NextDouble() < 0.66;
                float yaw = (float)(_random.NextDouble() * Math.Tau);
                player.Velocity = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw)) * 4.3f;
            }

            if (player.Walking)
            {
                player.Position += player.Velocity * dt;
                float yaw = MathF.Atan2(player.Velocity.X, player.Velocity.Z) + (float)(_random.NextDouble() - 0.5) * 0.2f;
                player.Facing = Vector3.Normalize(new Vector3(MathF.Sin(yaw), -0.35f, MathF.Cos(yaw)));
                if (_random.NextDouble() < 0.02)
                {
                    player.Position += new Vector3(0f, 1.2f, 0f);
                }
                else if (player.Position.Y > 2f)
                {
                    player.Position = player.Position with { Y = MathF.Max(2f, player.Position.Y - 9.8f * dt) };
                }
            }

            if (_random.NextDouble() < 0.03)
            {
                player.Health = Math.Max(0, player.Health - _random.Next(5, 20));
            }
            else if (player.Health < player.MaxHealth && _random.NextDouble() < 0.1)
            {
                player.Health++;
            }

            player.Alive = player.Health > 0;
            if (!player.Alive && _random.NextDouble() < 0.05)
            {
                player.Health = player.MaxHealth;
                player.Alive = true;
            }

            if (_random.NextDouble() < 0.02)
            {
                player.Mode = player.Mode == PlayerInteractionMode.Building ? PlayerInteractionMode.Combat : PlayerInteractionMode.Building;
            }

            if (_random.NextDouble() < 0.03)
            {
                PlayerProgressionSnapshot progression = player.Progression!.Value;
                int experience = progression.Experience + 25;
                player.Progression = experience >= progression.ExperienceToNextLevel
                    ? new PlayerProgressionSnapshot(progression.Level + 1, 0, progression.ExperienceToNextLevel + 500)
                    : progression with { Experience = experience };
                if (experience >= progression.ExperienceToNextLevel)
                {
                    PlayerCombatStatsSnapshot stats = player.Stats!.Value;
                    player.Stats = stats with { Strength = stats.Strength + 1, AttackSpeed = stats.AttackSpeed * 1.05f };
                }
            }

            player.Starved = _random.NextDouble() < 0.05 ? _random.Next(1, 3) : 0;
        }

        private void StepEnemy(ScriptedActor enemy, float dt)
        {
            if (!enemy.Alive)
            {
                return;
            }

            if (_random.NextDouble() < 0.05)
            {
                enemy.Behavior = (EnemyBehaviorState)_random.Next(0, 3);
                float yaw = (float)(_random.NextDouble() * Math.Tau);
                enemy.Velocity = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw)) * 3f;
            }

            if (enemy.Behavior != EnemyBehaviorState.Idle)
            {
                enemy.Position += enemy.Velocity * dt;
                enemy.Facing = Vector3.Normalize(enemy.Velocity);
            }

            if (_random.NextDouble() < 0.01)
            {
                enemy.Health = 0;
                enemy.Alive = false;
                enemy.Behavior = EnemyBehaviorState.Dead;
            }
        }

        private ScriptedActor NewPlayer(int index)
        {
            return new ScriptedActor
            {
                Id = _nextActorId++,
                Kind = CreatureKind.Player,
                SessionId = SessionId(index),
                Name = $"Player {index}",
                Position = new Vector3(100f + index * 3.3f, 2f, 200f - index * 1.7f),
                Facing = -Vector3.UnitZ,
                Health = 100,
                MaxHealth = 100,
                Alive = true,
                Walking = index % 3 != 0,
                Velocity = new Vector3(1f, 0f, 0.5f),
                Mode = PlayerInteractionMode.Building,
                Stats = new PlayerCombatStatsSnapshot(10, 2f, 10),
                Progression = new PlayerProgressionSnapshot(1, 0, 1000)
            };
        }

        private ScriptedActor NewEnemy()
        {
            return new ScriptedActor
            {
                Id = 5000 + _nextActorId++,
                Kind = CreatureKind.Enemy,
                Name = "Wolf",
                Position = new Vector3(120f + (float)_random.NextDouble() * 20f, 1f, 180f + (float)_random.NextDouble() * 20f),
                Facing = Vector3.UnitX,
                Health = 40,
                MaxHealth = 40,
                Alive = true,
                Behavior = EnemyBehaviorState.Idle
            };
        }
    }

    private sealed class ScriptedActor
    {
        public int Id;
        public CreatureKind Kind;
        public string? SessionId;
        public string Name = string.Empty;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Facing;
        public bool Walking;
        public int Health;
        public int MaxHealth;
        public bool Alive;
        public PlayerInteractionMode? Mode;
        public EnemyBehaviorState? Behavior;
        public PlayerCombatStatsSnapshot? Stats;
        public PlayerProgressionSnapshot? Progression;
        public int Starved;

        public ActorSnapshot ToSnapshot()
        {
            return new ActorSnapshot(Id, Kind, SessionId, Position, Facing, Health, MaxHealth, Alive, Mode, Stats, Name, Progression, Behavior, Starved);
        }
    }
}
