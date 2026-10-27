using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Runtime;

// MEM-3: the authoritative runtime tick on a headless server (three remote sessions, one wolf,
// no actions). Before MEM-3 (bytes per call, Debug, measured by these tests on the original
// code): AdvanceAuthoritativeTick 1806 with the wolf idle and 1806 with it chasing,
// ProcessQueuedAuthoritativeActions on an empty queue 432, TryResolveCombatActor for a player
// and the wolf 2824, and a tick plus ExportServerSnapshotEnvelope 4584. After: 30, 30, 0, 0 and
// 616 (the snapshot's own actor list and accepted-sequence dictionary, 584, plus the tick's 30).
// MEM-11 removed the last 30 B: the tick is 0 and tick plus export is exactly the wire payload.
public class WorldSessionRuntimeTickAllocationTests
{
    // Before MEM-11, Player.CheckAndLogCollisions ran a Bullet contact test for every player
    // every 0.5 s with a new list and native callback (about 47 B per tick over four players;
    // the minimum AllocationAssert round read 30 B), and the budget was 64 B. The report is
    // diagnostics-only and now runs only while the diagnostics log is enabled, so an idle or
    // chasing tick allocates nothing.
    private const long RuntimeTickBudgetBytes = 0;

    // Players are spawned far from the wolf's fixed spawn (5.5, 1, 2.5), out of its aggro range.
    private static readonly Vector3 PlayerStart = new(40f, 1.5f, 40f);

    private static readonly PlayerSessionId[] RemoteSessions =
    [
        new("session-a"),
        new("session-b"),
        new("session-c")
    ];

    private readonly ITestOutputHelper _output;

    public WorldSessionRuntimeTickAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AdvanceAuthoritativeTick_ThreeRemoteSessionsIdleWolf_AllocatesNothing()
    {
        using WorldSessionRuntime runtime = CreateSettledRuntime(out ulong tick);
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors);

        long bytes = AllocationAssert.MeasureBytesPerIteration(
            () => runtime.AdvanceAuthoritativeTick(
                ++tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault),
            iterations: 50);

        _output.WriteLine($"WorldSessionRuntime.AdvanceAuthoritativeTick, 3 sessions, idle wolf: {bytes} B");
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        Assert.True(
            bytes <= RuntimeTickBudgetBytes,
            $"Allocated {bytes} B per tick; budget is {RuntimeTickBudgetBytes} B.");
    }

    [Fact]
    public void AdvanceAuthoritativeTick_ThreeRemoteSessionsChasingWolf_AllocatesNothing()
    {
        using WorldSessionRuntime runtime = CreateSettledRuntime(out ulong tick);
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors);
        // Inside aggro range of the nearest player but outside attack range, put back every tick
        // so the wolf keeps chasing through the map's collision resolver and never attacks.
        Vector3 chaseStart = new(PlayerStart.X - 6f, 1f, PlayerStart.Z + 2f);

        long bytes = AllocationAssert.MeasureBytesPerIteration(
            () =>
            {
                wolf.SetPosition(chaseStart);
                runtime.AdvanceAuthoritativeTick(
                    ++tick,
                    SimulationClock.FixedDeltaSeconds,
                    default(PlayerCommandFrame),
                    PlayerSimulationTuning.AuthoritativeDefault);
            },
            iterations: 50);

        _output.WriteLine($"WorldSessionRuntime.AdvanceAuthoritativeTick, 3 sessions, chasing wolf: {bytes} B");
        Assert.Equal(EnemyBehaviorState.Chasing, wolf.BehaviorState);
        Assert.True(
            bytes <= RuntimeTickBudgetBytes,
            $"Allocated {bytes} B per tick; budget is {RuntimeTickBudgetBytes} B.");
    }

    [Fact]
    public void ProcessQueuedAuthoritativeActions_EmptyQueue_AllocatesNothing()
    {
        using WorldSessionRuntime runtime = CreateSettledRuntime(out ulong tick);
        List<QueuedPlayerAction> noActions = [];

        long bytes = AllocationAssert.AtMost(
            0,
            () => runtime.ProcessQueuedAuthoritativeActions(++tick, noActions));

        _output.WriteLine($"WorldSessionRuntime.ProcessQueuedAuthoritativeActions, empty: {bytes} B");
        Assert.Same(Array.Empty<AuthoritativeActionResult>(), runtime.ProcessQueuedAuthoritativeActions(++tick, noActions));
    }

    [Fact]
    public void TryResolveCombatActor_AllocatesNothing()
    {
        using WorldSessionRuntime runtime = CreateSettledRuntime(out _);
        int lastPlayerActorId = runtime.Sessions.Players.Max(player => player.ActorId);
        int wolfActorId = runtime.Enemies.ActiveActors[0].Combatant.Actor.Id;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            runtime.TryResolveCombatActor(lastPlayerActorId, out _);
            runtime.TryResolveCombatActor(wolfActorId, out _);
        });

        _output.WriteLine($"WorldSessionRuntime.TryResolveCombatActor, a player and the wolf: {bytes} B");
        Assert.True(runtime.TryResolveCombatActor(lastPlayerActorId, out _));
    }

    // The envelope's collections are the wire payload and stay fresh per snapshot: the actor
    // list (3 players and the wolf) and the accepted-sequence dictionary. Only the intermediates
    // are reused, and an empty mutation or damage batch is Array.Empty.
    [Fact]
    public void ExportServerSnapshotEnvelope_RestingTick_AllocatesOnlyTheWirePayload()
    {
        using WorldSessionRuntime runtime = CreateSettledRuntime(out ulong tick);

        long tickAndExportBytes = AllocationAssert.MeasureBytesPerIteration(
            () =>
            {
                runtime.AdvanceAuthoritativeTick(
                    ++tick,
                    SimulationClock.FixedDeltaSeconds,
                    default(PlayerCommandFrame),
                    PlayerSimulationTuning.AuthoritativeDefault);
                runtime.ExportServerSnapshotEnvelope();
            },
            iterations: 50);
        long exportBytes = AllocationAssert.MeasureBytesPerIteration(() => runtime.ExportServerSnapshotEnvelope());
        ServerSnapshotEnvelope envelope = runtime.ExportServerSnapshotEnvelope();
        long payloadBytes = MeasureWirePayloadBytes(envelope);

        _output.WriteLine(
            $"AdvanceAuthoritativeTick + ExportServerSnapshotEnvelope, 3 sessions: {tickAndExportBytes} B; " +
            $"export alone {exportBytes} B; its fresh actor list and dictionary {payloadBytes} B");
        Assert.Equal(4, envelope.Actors.Count);
        Assert.Same(Array.Empty<WorldMutationDelta>(), envelope.WorldMutations);
        Assert.Same(Array.Empty<DamageEventDelta>(), envelope.DamageEvents);
        Assert.NotSame(envelope.Actors, runtime.ExportServerSnapshotEnvelope().Actors);
        Assert.True(exportBytes <= payloadBytes, $"Export allocated {exportBytes} B; its wire payload is {payloadBytes} B.");
        Assert.True(
            tickAndExportBytes <= payloadBytes + RuntimeTickBudgetBytes,
            $"Tick and export allocated {tickAndExportBytes} B; budget is {payloadBytes + RuntimeTickBudgetBytes} B.");
    }

    // A server tick as the dedicated server runs it with idle clients: every session's command
    // is accepted, so the snapshot carries three accepted sequences.
    [Fact]
    public void ExportServerSnapshotEnvelope_IdleCommandsFromEverySession_AllocatesOnlyTheWirePayload()
    {
        using WorldSessionRuntime runtime = CreateSettledRuntime(out ulong tick);
        PlayerSessionId primary = runtime.PrimaryRemoteSessionId!.Value;

        long tickAndExportBytes = AllocationAssert.MeasureBytesPerIteration(
            () =>
            {
                ++tick;
                PlayerCommandFrame primaryFrame = default;
                foreach (PlayerSessionId session in RemoteSessions)
                {
                    PlayerCommandFrame frame = PlayerCommandFrame.CreateIdle(session, tick);
                    if (session == primary)
                    {
                        primaryFrame = frame;
                    }
                    else
                    {
                        runtime.AdvanceSecondaryRemoteSession(frame, SimulationClock.FixedDeltaSeconds, PlayerSimulationTuning.AuthoritativeDefault);
                    }
                }

                runtime.AdvanceAuthoritativeTick(
                    tick,
                    SimulationClock.FixedDeltaSeconds,
                    primaryFrame,
                    PlayerSimulationTuning.AuthoritativeDefault);
                runtime.ExportServerSnapshotEnvelope();
            },
            iterations: 50);
        ServerSnapshotEnvelope envelope = runtime.ExportServerSnapshotEnvelope();
        long payloadBytes = MeasureWirePayloadBytes(envelope);

        _output.WriteLine(
            $"Idle commands, tick and export, 3 sessions: {tickAndExportBytes} B; fresh actor list and dictionary {payloadBytes} B");
        Assert.Equal(3, envelope.LastAcceptedCommandSequences.Count);
        Assert.True(
            tickAndExportBytes <= payloadBytes + RuntimeTickBudgetBytes,
            $"Allocated {tickAndExportBytes} B per tick; budget is {payloadBytes + RuntimeTickBudgetBytes} B.");
    }

    // What a fresh, exactly sized actor list and accepted-sequence dictionary of this envelope's
    // shape cost to build: the part of every snapshot that is handed off and must not be reused.
    private static long MeasureWirePayloadBytes(ServerSnapshotEnvelope envelope)
    {
        int actorCount = envelope.Actors.Count;
        KeyValuePair<string, ulong>[] sequences = envelope.LastAcceptedCommandSequences.ToArray();
        return AllocationAssert.MeasureBytesPerIteration(() =>
        {
            List<ActorSnapshot> actors = new(actorCount);
            Dictionary<string, ulong> accepted = new(sequences.Length);
            foreach (KeyValuePair<string, ulong> sequence in sequences)
            {
                accepted[sequence.Key] = sequence.Value;
            }

            GC.KeepAlive(actors);
            GC.KeepAlive(accepted);
        });
    }

    private static WorldSessionRuntime CreateSettledRuntime(out ulong tick)
    {
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            PlayerStart,
            new SceneEventHub(),
            loadVisualAssets: false);
        foreach (PlayerSessionId session in RemoteSessions)
        {
            runtime.AttachRemoteSession(session, session.Value);
        }

        // Let the bodies land and come to rest, and drain the join-time deltas.
        tick = 0;
        for (int i = 0; i < 240; i++)
        {
            runtime.AdvanceAuthoritativeTick(
                ++tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault);
        }

        runtime.ExportServerSnapshotEnvelope();
        return runtime;
    }
}
