using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Determinism;

// NET-1: combat damage and crit rolls are seeded and counter-based, so a hit's result depends only
// on (simulation seed, tick, session, action sequence, target), never on Random.Shared or on the
// order a tick processes its actions in.
public class CombatDeterminismTests
{
    private const int HitCount = 24;
    private const int EnemyActorId = 4100;
    private const int EnemyMaxHealth = 1_000_000;
    private static readonly PlayerSessionId SessionA = new("session-a");
    private static readonly PlayerSessionId SessionB = new("session-b");

    [Fact]
    public void Constructor_WithoutSeed_UsesDocumentedDefaultSeed()
    {
        using WorldSessionRuntime runtime = CreateRuntime(new SceneEventHub());
        using WorldSessionRuntime seeded = CreateRuntime(new SceneEventHub(), simulationSeed: 77UL);

        Assert.Equal(WorldSessionRuntime.DefaultSimulationSeed, runtime.SimulationSeed);
        Assert.Equal(77UL, seeded.SimulationSeed);
    }

    [Fact]
    public void SameSeed_SameHitSequence_ReportsIdenticalDamageAppliedEvents()
    {
        IReadOnlyList<(int Amount, FloatingCombatTextKind Kind)> first = RunHitSequence(simulationSeed: 1234UL);
        IReadOnlyList<(int Amount, FloatingCombatTextKind Kind)> second = RunHitSequence(simulationSeed: 1234UL);

        Assert.Equal(HitCount, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSeeds_SameHitSequence_DivergeOverManyHits()
    {
        IReadOnlyList<(int Amount, FloatingCombatTextKind Kind)> first = RunHitSequence(simulationSeed: 1234UL);
        IReadOnlyList<(int Amount, FloatingCombatTextKind Kind)> second = RunHitSequence(simulationSeed: 98765UL);

        Assert.Equal(HitCount, first.Count);
        Assert.Equal(HitCount, second.Count);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void HitSequence_StaysWithinDerivedDamageRange()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = CreateRuntime(events);
        PlayerCombatDerivedStats stats = runtime.Player.Combatant.DerivedStats;

        IReadOnlyList<(int Amount, FloatingCombatTextKind Kind)> hits = RunHitSequence(simulationSeed: 5UL);

        Assert.All(hits, hit =>
        {
            if (hit.Kind == FloatingCombatTextKind.Critical)
            {
                Assert.Equal(stats.CriticalDamage, hit.Amount);
            }
            else
            {
                Assert.InRange(hit.Amount, stats.MinDamage, stats.MaxDamage);
            }
        });
        Assert.True(hits.Select(hit => hit.Amount).Distinct().Count() > 1);
    }

    [Fact]
    public void SameSeed_DifferentProcessingOrder_GivesEachSessionTheSameRolls()
    {
        IReadOnlyDictionary<string, int> aFirst = RunTwoSessionTick(sessionAReceivedFirst: true);
        IReadOnlyDictionary<string, int> bFirst = RunTwoSessionTick(sessionAReceivedFirst: false);

        Assert.Equal(2, aFirst.Count);
        Assert.True(aFirst[SessionA.Value] > 0);
        Assert.True(aFirst[SessionB.Value] > 0);
        Assert.Equal(aFirst[SessionA.Value], bFirst[SessionA.Value]);
        Assert.Equal(aFirst[SessionB.Value], bFirst[SessionB.Value]);
    }

    [Fact]
    public void SeedCombine_FiveComponents_IsOrderSensitiveAndMatchesNestedCombine()
    {
        ulong combined = Seed.Combine(1UL, 2UL, 3UL, 4UL, 5UL);

        Assert.Equal(Seed.Combine(Seed.Combine(1UL, 2UL, 3UL, 4UL), 5UL), combined);
        Assert.NotEqual(combined, Seed.Combine(1UL, 2UL, 3UL, 5UL, 4UL));
        Assert.Equal(ulong.MaxValue, Seed.FromInt(-1));
        Assert.Equal(42UL, Seed.FromInt(42));
    }

    private static IReadOnlyList<(int Amount, FloatingCombatTextKind Kind)> RunHitSequence(ulong simulationSeed)
    {
        SceneEventHub events = new();
        List<(int Amount, FloatingCombatTextKind Kind)> applied = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(
            damage => applied.Add((damage.Amount, damage.Kind)));
        using WorldSessionRuntime runtime = CreateRuntime(events, simulationSeed);
        runtime.AttachRemoteSession(SessionA, "Player A");
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        EnemyActor enemy = ReplaceEnemies(runtime, events, runtime.Player.GetWorldPosition() + new Vector3(1.5f, 0f, 0f));

        int ticksPerAttack = (int)Math.Ceiling(
            runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds / SimulationClock.FixedDeltaSeconds) + 1;
        ulong tick = 0;
        for (int hit = 1; hit <= HitCount; hit++)
        {
            for (int step = 0; step < ticksPerAttack; step++)
            {
                tick++;
                runtime.AdvanceAuthoritativeTick(
                    tick,
                    SimulationClock.FixedDeltaSeconds,
                    (PlayerCommandFrame?)null,
                    CreatePlayerTuning(),
                    allowGameplaySimulation: false);
            }

            runtime.Player.SetFacing(DirectionFrom(
                runtime.Player.Camera.Position,
                enemy.Combatant.GetCombatAnchorWorldPosition()));
            AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
                SessionA,
                (ulong)hit,
                new CombatUseIntent(Vector3.Zero, Vector3.UnitX)));

            Assert.True(result.Accepted, result.Rejection?.ToString());
            Assert.Equal(EnemyActorId, Assert.IsType<CombatResolutionResult>(result.Payload).TargetActorId);
        }

        return applied;
    }

    private static IReadOnlyDictionary<string, int> RunTwoSessionTick(bool sessionAReceivedFirst)
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = CreateRuntime(events, simulationSeed: 31337UL);
        runtime.AttachRemoteSession(SessionA, "Player A");
        runtime.AttachRemoteSession(SessionB, "Player B");
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Vector3 secondaryPosition = FindActorPosition(runtime.ExportServerSnapshotEnvelope(), SessionB.Value);
        Vector3 enemyPosition = (runtime.Player.GetWorldPosition() + secondaryPosition) * 0.5f;
        EnemyActor enemy = ReplaceEnemies(runtime, events, enemyPosition);
        Vector3 anchor = enemy.Combatant.GetCombatAnchorWorldPosition();

        runtime.Player.SetFacing(DirectionFrom(runtime.Player.Camera.Position, anchor));
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                SessionB,
                1,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                new PlayerLookIntent(0f, 0f, DirectionFrom(secondaryPosition, anchor)),
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        QueuedPlayerAction actionA = new(
            sessionAReceivedFirst ? 1UL : 2UL,
            SessionA,
            new PlayerActionMessage(SessionA, 11, new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));
        QueuedPlayerAction actionB = new(
            sessionAReceivedFirst ? 2UL : 1UL,
            SessionB,
            new PlayerActionMessage(SessionB, 12, new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));

        IReadOnlyList<AuthoritativeActionResult> results = runtime.ProcessQueuedAuthoritativeActions(
            9,
            sessionAReceivedFirst ? [actionA, actionB] : [actionB, actionA]);

        Assert.Equal(sessionAReceivedFirst ? SessionA.Value : SessionB.Value, results[0].SessionId);
        Dictionary<string, int> damageBySession = new(StringComparer.Ordinal);
        foreach (AuthoritativeActionResult result in results)
        {
            Assert.True(result.Accepted, result.Rejection?.ToString());
            CombatResolutionResult payload = Assert.IsType<CombatResolutionResult>(result.Payload);
            Assert.Equal(EnemyActorId, payload.TargetActorId);
            damageBySession[result.SessionId] = payload.DamageApplied;
        }

        return damageBySession;
    }

    private static EnemyActor ReplaceEnemies(WorldSessionRuntime runtime, SceneEventHub events, Vector3 position)
    {
        runtime.Enemies.Clear();
        EnemyCombatant combatant = new(
            () => position,
            events,
            actorId: EnemyActorId,
            maxHealth: EnemyMaxHealth,
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
        return enemy;
    }

    private static WorldSessionRuntime CreateRuntime(SceneEventHub events, ulong? simulationSeed = null)
    {
        return simulationSeed.HasValue
            ? new WorldSessionRuntime(
                floorSize: 10,
                playerStartPosition: new Vector3(5f, 1.5f, 5f),
                events,
                simulationSeed: simulationSeed.Value)
            : new WorldSessionRuntime(
                floorSize: 10,
                playerStartPosition: new Vector3(5f, 1.5f, 5f),
                events);
    }

    private static Vector3 FindActorPosition(ServerSnapshotEnvelope snapshot, string sessionId)
    {
        return Assert.Single(snapshot.Actors, actor => actor.SessionId == sessionId).Position;
    }

    private static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? -Vector3.UnitZ : direction.Normalized();
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
}
