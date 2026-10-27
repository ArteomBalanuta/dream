using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server;

// NET-6: the authoritative tick processes sessions in ordinal session-id order. Join order, the
// order a caller collects command frames in and ConcurrentDictionary enumeration order (which
// follows per-process randomized string hashes) must not change who resolves first.
public class CanonicalTickOrderTests
{
    private const int EnemyActorId = 4200;
    private const string HostSession = "session-host";
    private const string SessionB = "session-b";
    private const string SessionC = "session-c";

    [Fact]
    public void DedicatedServerHost_JoinedSessions_DriveTheTickInOrdinalOrder()
    {
        RecordingTickAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority);
        string[] joinOrder = ["session-b", "session-9", "Session-A", "session-a", "session-10"];
        for (int i = 0; i < joinOrder.Length; i++)
        {
            host.HandleClientMessage($"connection-{joinOrder.Length - i}", new ClientJoinRequestMessage(joinOrder[i], joinOrder[i]));
        }

        for (int i = 0; i < joinOrder.Length; i++)
        {
            host.HandleClientMessage($"connection-{joinOrder.Length - i}", CreateIdleCommand(joinOrder[i], 1));
        }

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(
            ["Session-A", "session-10", "session-9", "session-a", "session-b"],
            authority.LastTickCommands.Select(frame => frame.SessionId.Value).ToArray());
    }

    [Fact]
    public void DedicatedServerHost_SessionJoinedAfterTicking_IsInsertedInOrdinalOrder()
    {
        RecordingTickAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority);
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-z", "z"));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-m", "m"));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        host.HandleClientMessage("connection-3", new ClientJoinRequestMessage("session-a", "a"));
        host.HandleClientMessage("connection-1", CreateIdleCommand("session-z", 2));
        host.HandleClientMessage("connection-2", CreateIdleCommand("session-m", 2));
        host.HandleClientMessage("connection-3", CreateIdleCommand("session-a", 2));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(
            ["session-a", "session-m", "session-z"],
            authority.LastTickCommands.Select(frame => frame.SessionId.Value).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RuntimeHost_SimultaneousLethalHits_CreditTheOrdinallyFirstSession(bool joinBFirst)
    {
        (AuthoritativeActionResult[] results, int enemyHealth) = RunRuntimeHostLethalTick(joinBFirst);

        AssertOrdinalKillCredit(results, enemyHealth);
    }

    [Fact]
    public void RuntimeHost_SimultaneousLethalHits_AreIndependentOfJoinAndFrameOrder()
    {
        (AuthoritativeActionResult[] bFirst, _) = RunRuntimeHostLethalTick(joinBFirst: true);
        (AuthoritativeActionResult[] cFirst, _) = RunRuntimeHostLethalTick(joinBFirst: false);

        Assert.Equal(DescribeKills(bFirst), DescribeKills(cFirst));
        Assert.Equal(bFirst.Select(result => result.SessionId), cFirst.Select(result => result.SessionId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DedicatedServerHost_SimultaneousLethalHits_CreditTheOrdinallyFirstSession(bool joinBFirst)
    {
        (AuthoritativeActionResult[] results, int enemyHealth) = RunDedicatedHostLethalTick(joinBFirst);

        AssertOrdinalKillCredit(results, enemyHealth);
    }

    [Fact]
    public void DedicatedServerHost_SimultaneousLethalHits_AreIndependentOfJoinOrder()
    {
        (AuthoritativeActionResult[] bFirst, _) = RunDedicatedHostLethalTick(joinBFirst: true);
        (AuthoritativeActionResult[] cFirst, _) = RunDedicatedHostLethalTick(joinBFirst: false);

        Assert.Equal(DescribeKills(bFirst), DescribeKills(cFirst));
        Assert.Equal(bFirst.Select(result => result.SessionId), cFirst.Select(result => result.SessionId));
    }

    [Fact]
    public void Runtime_SecondaryPlayers_AreCapturedInOrdinalOrderAfterAttachAndDetach()
    {
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false);
        runtime.AttachRemoteSession(new PlayerSessionId(HostSession), "Host");
        runtime.AttachRemoteSession(new PlayerSessionId("session-z"), "Z");
        runtime.AttachRemoteSession(new PlayerSessionId(SessionC), "C");
        runtime.AttachRemoteSession(new PlayerSessionId("session-m"), "M");
        runtime.AttachRemoteSession(new PlayerSessionId(SessionB), "B");

        Assert.Equal(
            [HostSession, SessionB, SessionC, "session-m", "session-z"],
            runtime.CaptureActorSnapshots()
                .Where(actor => actor.Kind == CreatureKind.Player)
                .Select(actor => actor.SessionId));

        runtime.DetachRemoteSession(new PlayerSessionId(SessionC));
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "A");

        Assert.Equal(
            [HostSession, "session-a", SessionB, "session-m", "session-z"],
            runtime.CaptureActorSnapshots()
                .Where(actor => actor.Kind == CreatureKind.Player)
                .Select(actor => actor.SessionId));
    }

    private static void AssertOrdinalKillCredit(AuthoritativeActionResult[] results, int enemyHealth)
    {
        Assert.Equal([SessionB, SessionC], results.Select(result => result.SessionId));
        Assert.All(results, result => Assert.True(result.Accepted));
        CombatResolutionResult killingBlow = Assert.IsType<CombatResolutionResult>(results[0].Payload);
        Assert.Equal(EnemyActorId, killingBlow.TargetActorId);
        Assert.True(killingBlow.TargetDied);
        CombatResolutionResult lateHit = Assert.IsType<CombatResolutionResult>(results[1].Payload);
        Assert.NotEqual(EnemyActorId, lateHit.TargetActorId);
        Assert.Equal(0, enemyHealth);
    }

    private static string[] DescribeKills(AuthoritativeActionResult[] results)
    {
        return results
            .Select(result => (result.SessionId, Payload: Assert.IsType<CombatResolutionResult>(result.Payload)))
            .Where(entry => entry.Payload.TargetActorId == EnemyActorId)
            .Select(entry => $"{entry.SessionId}:{entry.Payload.DamageApplied}:{entry.Payload.TargetDied}")
            .ToArray();
    }

    private static (AuthoritativeActionResult[] Results, int EnemyHealth) RunRuntimeHostLethalTick(bool joinBFirst)
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        string first = joinBFirst ? SessionB : SessionC;
        string second = joinBFirst ? SessionC : SessionB;
        host.JoinSession(HostSession, "Host");
        host.JoinSession(first, first);
        host.JoinSession(second, second);

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        EnemyActor enemy = PlaceWoundedEnemy(runtime, events, snapshot);
        Vector3 anchor = enemy.Combatant.GetCombatAnchorWorldPosition();

        IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [
                CreateAttackCommand(first, FindActorPosition(snapshot, first), anchor),
                CreateAttackCommand(second, FindActorPosition(snapshot, second), anchor)
            ],
            CreatePlayerTuning(),
            allowGameplaySimulation: false);

        return (results.ToArray(), enemy.Combatant.Health.CurrentHealth);
    }

    private static (AuthoritativeActionResult[] Results, int EnemyHealth) RunDedicatedHostLethalTick(bool joinBFirst)
    {
        SceneEventHub events = new();
        WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using DedicatedServerHost host = CreateHost(new RuntimeAuthoritativeSessionHost(runtime));
        string first = joinBFirst ? SessionB : SessionC;
        string second = joinBFirst ? SessionC : SessionB;
        host.HandleClientMessage("connection-host", new ClientJoinRequestMessage(HostSession, "Host"));
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage(first, first));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage(second, second));

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        EnemyActor enemy = PlaceWoundedEnemy(runtime, events, snapshot);
        Vector3 anchor = enemy.Combatant.GetCombatAnchorWorldPosition();
        host.HandleClientMessage(
            "connection-1",
            new PlayerCommandMessage(CreateAttackCommand(first, FindActorPosition(snapshot, first), anchor)));
        host.HandleClientMessage(
            "connection-2",
            new PlayerCommandMessage(CreateAttackCommand(second, FindActorPosition(snapshot, second), anchor)));

        Assert.Equal(1, host.AdvanceTime(SimulationClock.FixedDeltaSeconds));

        return (host.DrainPendingActionResults().ToArray(), enemy.Combatant.Health.CurrentHealth);
    }

    // Both secondaries spawn on the same lane; the enemy stands in front of the pair, within reach
    // of each, and any hit kills it.
    private static EnemyActor PlaceWoundedEnemy(WorldSessionRuntime runtime, SceneEventHub events, ServerSnapshotEnvelope snapshot)
    {
        Vector3 between = (FindActorPosition(snapshot, SessionB) + FindActorPosition(snapshot, SessionC)) * 0.5f;
        Vector3 position = between + new Vector3(0f, 0f, 1.5f);
        runtime.Enemies.Clear();
        EnemyCombatant combatant = new(
            () => position,
            events,
            actorId: EnemyActorId,
            maxHealth: 100,
            attackDamage: 1,
            attackIntervalSeconds: 1f);
        EnemyActor enemy = new(
            combatant,
            position,
            aggroRange: 0f,
            attackRange: 0f,
            moveSpeed: 0f,
            displayName: "Wolf");
        runtime.Enemies.Add(enemy);
        combatant.ApplyDamage(99);
        return enemy;
    }

    private static PlayerCommandFrame CreateAttackCommand(string sessionId, Vector3 position, Vector3 anchor)
    {
        Vector3 direction = anchor - position;
        return new PlayerCommandFrame(
            new PlayerSessionId(sessionId),
            1,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            PlayerMoveIntent.None,
            new PlayerLookIntent(0f, 0f, direction.Normalized()),
            PrimaryUsePressed: true);
    }

    private static PlayerCommandMessage CreateIdleCommand(string sessionId, ulong sequence)
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

    private static Vector3 FindActorPosition(ServerSnapshotEnvelope snapshot, string sessionId)
    {
        return Assert.Single(snapshot.Actors, actor => actor.SessionId == sessionId).Position;
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

    private static PlayerSimulationTuning CreatePlayerTuning()
    {
        return new PlayerSimulationTuning(
            EyeHeightOffset: 0.35f,
            MouseSensitivity: 2.5f,
            MoveSpeed: 6f,
            NoclipSpeed: 150f,
            JumpVelocity: 5f);
    }

    // Records the command frames exactly as the host hands them over, without reordering them.
    private sealed class RecordingTickAuthority : ITickDrivenAuthoritativeSessionHost
    {
        public List<PlayerCommandFrame> LastTickCommands { get; } = [];

        public ulong CurrentServerTick { get; private set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 101, CurrentServerTick, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            throw new InvalidOperationException("Actions are queued for the tick.");
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return new ServerSnapshotEnvelope(
                CurrentServerTick,
                new Dictionary<string, ulong>(),
                Array.Empty<ActorSnapshot>(),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>());
        }

        public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
            ulong serverTick,
            float deltaTime,
            IReadOnlyList<PlayerCommandFrame> commandFrames,
            IReadOnlyList<QueuedPlayerAction> queuedActions,
            PlayerSimulationTuning playerTuning,
            bool allowGameplaySimulation = true)
        {
            CurrentServerTick = serverTick;
            LastTickCommands.Clear();
            LastTickCommands.AddRange(commandFrames);
            return Array.Empty<AuthoritativeActionResult>();
        }
    }
}
