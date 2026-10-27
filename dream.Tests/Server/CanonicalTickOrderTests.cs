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
    private const string SessionA = "session-a";
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

    [Fact]
    public void DedicatedServerHost_DeferredJoins_AreAppliedAtTheTickInOrdinalOrder()
    {
        RecordingTickAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority, deferJoinsToTick: true);
        string[] arrivalOrder = ["session-z", "session-b", "Session-A", "session-m"];
        for (int i = 0; i < arrivalOrder.Length; i++)
        {
            Assert.Empty(host.HandleClientMessage($"connection-{i}", new ClientJoinRequestMessage(arrivalOrder[i], arrivalOrder[i])));
        }

        Assert.Empty(authority.JoinOrder);
        Assert.False(host.IsConnectionJoined("connection-0"));

        Assert.Equal(1, host.AdvanceTime(SimulationClock.FixedDeltaSeconds));

        Assert.Equal(["Session-A", "session-b", "session-m", "session-z"], authority.JoinOrder);
        Assert.True(host.IsConnectionJoined("connection-0"));
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.Equal(
            ["connection-2:Session-A", "connection-1:session-b", "connection-3:session-m", "connection-0:session-z"],
            replies.Select(reply => $"{reply.ConnectionId}:{Assert.IsType<JoinAcceptedMessage>(reply.Message).SessionId}"));

        host.DrainPendingJoinRepliesInto(replies);
        Assert.Empty(replies);
    }

    [Fact]
    public void DedicatedServerHost_DeferredJoin_FromAReleasedConnection_IsDropped()
    {
        RecordingTickAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority, deferJoinsToTick: true);
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "a"));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-b", "b"));
        host.ReleaseConnection("connection-1");

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(["session-b"], authority.JoinOrder);
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.Equal("connection-2", Assert.Single(replies).ConnectionId);
    }

    [Fact]
    public void DedicatedServerHost_ByDefault_AppliesJoinsImmediately()
    {
        RecordingTickAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority);

        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage(
            "connection-1",
            new ClientJoinRequestMessage("session-a", "a"));

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies));
        Assert.Equal(["session-a"], authority.JoinOrder);
        Assert.False(host.DefersJoinsToTick);
    }

    // Same-tick joins in different arrival orders produce the same seats, actor ids and spawn
    // points on a real runtime.
    [Fact]
    public void DedicatedServerHost_DeferredJoins_InDifferentArrivalOrders_GiveTheSameSeatsIdsAndSpawns()
    {
        string[] Describe(string[] arrivalOrder)
        {
            WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            using DedicatedServerHost host = CreateHost(new RuntimeAuthoritativeSessionHost(runtime), deferJoinsToTick: true);
            for (int i = 0; i < arrivalOrder.Length; i++)
            {
                host.HandleClientMessage($"connection-{arrivalOrder[i]}", new ClientJoinRequestMessage(arrivalOrder[i], arrivalOrder[i]));
            }

            Assert.Null(runtime.PrimaryRemoteSessionId);
            host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            return
            [
                $"primary={runtime.PrimaryRemoteSessionId?.Value}",
                .. runtime.CaptureActorSnapshots()
                    .Where(actor => actor.Kind == CreatureKind.Player)
                    .Select(actor => $"{actor.SessionId}#{actor.ActorId}@{actor.Position}")
                    .Order(StringComparer.Ordinal)
            ];
        }

        string[] forward = Describe([SessionA, SessionB, SessionC]);
        string[] reversed = Describe([SessionC, SessionB, SessionA]);

        Assert.Equal($"primary={SessionA}", forward[0]);
        Assert.Equal(forward, reversed);
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

    // The first session to join holds the primary seat (the runtime's Player body). Its primary use
    // used to resolve inline in the runtime tick, before every secondary's, so the seat holder won
    // every same-tick race. Now all seats resolve in one ordinal pass.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RuntimeHost_PrimarySeatAndSecondaryLethalHits_CreditTheOrdinallyFirstSession(bool ordinallyFirstHoldsPrimarySeat)
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        string seatHolder = ordinallyFirstHoldsPrimarySeat ? SessionA : SessionB;
        string secondary = ordinallyFirstHoldsPrimarySeat ? SessionB : SessionA;
        host.JoinSession(seatHolder, seatHolder);
        host.JoinSession(secondary, secondary);
        Assert.Equal(seatHolder, runtime.PrimaryRemoteSessionId?.Value);

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        EnemyActor enemy = PlaceWoundedEnemyBetween(
            runtime,
            events,
            FindActorPosition(snapshot, SessionA),
            FindActorPosition(snapshot, SessionB));
        Vector3 anchor = enemy.Combatant.GetCombatAnchorWorldPosition();

        IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [
                CreateAttackCommand(SessionB, FindActorPosition(snapshot, SessionB), anchor),
                CreateAttackCommand(SessionA, FindActorPosition(snapshot, SessionA), anchor)
            ],
            CreatePlayerTuning(),
            allowGameplaySimulation: false);

        Assert.Equal([SessionA, SessionB], results.Select(result => result.SessionId));
        Assert.All(results, result => Assert.True(result.Accepted));
        CombatResolutionResult killingBlow = Assert.IsType<CombatResolutionResult>(results[0].Payload);
        Assert.Equal(EnemyActorId, killingBlow.TargetActorId);
        Assert.True(killingBlow.TargetDied);
        CombatResolutionResult lateHit = Assert.IsType<CombatResolutionResult>(results[1].Payload);
        Assert.NotEqual(EnemyActorId, lateHit.TargetActorId);
        Assert.Equal(0, enemy.Combatant.Health.CurrentHealth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DedicatedServerHost_PrimarySeatAndSecondaryLethalHits_CreditTheOrdinallyFirstSession(bool ordinallyFirstHoldsPrimarySeat)
    {
        SceneEventHub events = new();
        WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using DedicatedServerHost host = CreateHost(new RuntimeAuthoritativeSessionHost(runtime));
        string seatHolder = ordinallyFirstHoldsPrimarySeat ? SessionA : SessionB;
        string secondary = ordinallyFirstHoldsPrimarySeat ? SessionB : SessionA;
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage(seatHolder, seatHolder));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage(secondary, secondary));
        Assert.Equal(seatHolder, runtime.PrimaryRemoteSessionId?.Value);

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        EnemyActor enemy = PlaceWoundedEnemyBetween(
            runtime,
            events,
            FindActorPosition(snapshot, SessionA),
            FindActorPosition(snapshot, SessionB));
        Vector3 anchor = enemy.Combatant.GetCombatAnchorWorldPosition();
        host.HandleClientMessage(
            "connection-2",
            new PlayerCommandMessage(CreateAttackCommand(secondary, FindActorPosition(snapshot, secondary), anchor)));
        host.HandleClientMessage(
            "connection-1",
            new PlayerCommandMessage(CreateAttackCommand(seatHolder, FindActorPosition(snapshot, seatHolder), anchor)));

        Assert.Equal(1, host.AdvanceTime(SimulationClock.FixedDeltaSeconds));

        AuthoritativeActionResult[] results = host.DrainPendingActionResults().ToArray();
        Assert.Equal([SessionA, SessionB], results.Select(result => result.SessionId));
        CombatResolutionResult killingBlow = Assert.IsType<CombatResolutionResult>(results[0].Payload);
        Assert.Equal(EnemyActorId, killingBlow.TargetActorId);
        Assert.True(killingBlow.TargetDied);
        Assert.Equal(0, enemy.Combatant.Health.CurrentHealth);
    }

    // Two players place into the same cell in the same tick: the ordinally first session gets the
    // cube and the other is rejected, whichever of them holds the primary seat.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RuntimeHost_PrimarySeatAndSecondaryBuildIntoSameCell_TheOrdinallyFirstSessionPlaces(bool ordinallyFirstHoldsPrimarySeat)
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        string seatHolder = ordinallyFirstHoldsPrimarySeat ? SessionA : SessionB;
        string secondary = ordinallyFirstHoldsPrimarySeat ? SessionB : SessionA;
        host.JoinSession(seatHolder, seatHolder);
        host.JoinSession(secondary, secondary);
        PlayerSimulationTuning tuning = CreatePlayerTuning();

        // One idle tick settles both bodies and their eyes.
        host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [CreateBuildCommand(SessionA, 1, Vector3.UnitZ, press: false), CreateBuildCommand(SessionB, 1, Vector3.UnitZ, press: false)],
            tuning,
            allowGameplaySimulation: false);

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        Vector3 eyeA = FindActorPosition(snapshot, SessionA) + new Vector3(0f, tuning.EyeHeightOffset, 0f);
        Vector3 eyeB = FindActorPosition(snapshot, SessionB) + new Vector3(0f, tuning.EyeHeightOffset, 0f);
        Vector3 target = FindSharedPlacementPoint(eyeA, eyeB, runtime.Map);

        IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
            serverTick: 2,
            SimulationClock.FixedDeltaSeconds,
            [
                CreateBuildCommand(SessionB, 2, (target - eyeB).Normalized(), press: true),
                CreateBuildCommand(SessionA, 2, (target - eyeA).Normalized(), press: true)
            ],
            tuning,
            allowGameplaySimulation: false);

        Assert.Equal([SessionA, SessionB], results.Select(result => result.SessionId));
        Assert.True(results[0].Accepted, "The ordinally first session should place the cube.");
        WorldMutationResult placed = Assert.IsType<WorldMutationResult>(results[0].Payload);
        Assert.Equal(WorldMutationKind.CubePlaced, placed.Kind);
        Assert.Equal(new Vector3(MathF.Floor(target.X), MathF.Floor(target.Y), MathF.Floor(target.Z)), placed.CellPosition);
        Assert.False(results[1].Accepted, "The second placement into the same cell should be rejected.");
    }

    [Fact]
    public void Runtime_SecondaryPlayers_AreCapturedInOrdinalOrderAfterAttachAndDetach()
    {
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
        WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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

    private static EnemyActor PlaceWoundedEnemyBetween(WorldSessionRuntime runtime, SceneEventHub events, Vector3 first, Vector3 second)
    {
        Vector3 between = (first + second) * 0.5f;
        Vector3 position = between + new Vector3(0f, 0f, 0.5f);
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

    // A build use places at eye + facing * 2. Returns a point exactly 2 from both eyes (on the
    // circle where the two spheres meet), picked as far from every cell boundary as possible, so
    // both players' placements land in the same cell.
    private static Vector3 FindSharedPlacementPoint(Vector3 eyeA, Vector3 eyeB, Map map)
    {
        const float placementDistance = 2f;
        Vector3 axis = eyeB - eyeA;
        float halfDistance = axis.Length * 0.5f;
        Assert.True(halfDistance < placementDistance, "The two players stand too far apart to reach one cell.");
        Vector3 normal = axis.Normalized();
        Vector3 basisU = Vector3.Cross(normal, Vector3.UnitY).Normalized();
        Vector3 basisV = Vector3.Cross(normal, basisU);
        Vector3 center = (eyeA + eyeB) * 0.5f;
        float radius = MathF.Sqrt(placementDistance * placementDistance - halfDistance * halfDistance);

        Vector3 best = default;
        float bestMargin = -1f;
        for (int step = 0; step < 720; step++)
        {
            float angle = step * MathF.PI / 360f;
            Vector3 point = center + radius * (MathF.Cos(angle) * basisU + MathF.Sin(angle) * basisV);
            if (point.Y < 1f || point.X < 0f || point.Z < 0f || point.X >= map.Width || point.Y >= map.Height || point.Z >= map.Depth)
            {
                continue;
            }

            float margin = MathF.Min(CellMargin(point.X), MathF.Min(CellMargin(point.Y), CellMargin(point.Z)));
            if (margin > bestMargin)
            {
                bestMargin = margin;
                best = point;
            }
        }

        Assert.True(bestMargin > 0.1f, $"No placement point clear of cell boundaries (best margin {bestMargin}).");
        return best;
    }

    private static float CellMargin(float value)
    {
        float fraction = value - MathF.Floor(value);
        return MathF.Min(fraction, 1f - fraction);
    }

    private static PlayerCommandFrame CreateBuildCommand(string sessionId, ulong sequence, Vector3 facing, bool press)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(sessionId),
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            PlayerMoveIntent.None,
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: press);
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

    private static DedicatedServerHost CreateHost(ITickDrivenAuthoritativeSessionHost authority, bool deferJoinsToTick = false)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(authority, router, deferJoinsToTick);
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

        public List<string> JoinOrder { get; } = [];

        public ulong CurrentServerTick { get; private set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            JoinOrder.Add(requestedSessionId);
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
