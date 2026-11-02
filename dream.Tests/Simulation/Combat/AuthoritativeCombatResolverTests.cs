using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Combat;

// ARCH-3 step B: combat resolution moved out of WorldSessionRuntime into AuthoritativeCombatResolver.
// These tests drive the resolver with no runtime, pin the runtime path's seeded rolls to values
// recorded on the pre-extraction code, and check the single-cell fracture capture against the old
// capture-everything-then-filter path.
public class AuthoritativeCombatResolverTests(ITestOutputHelper output)
{
    private const ulong GoldenSeed = 20261028UL;
    private const int EnemyActorId = 4100;
    private static readonly PlayerSessionId SessionA = new("session-a");

    // Recorded on 91449a7 (before the extraction) by the same hit sequence as
    // RuntimePath_SeededRolls_MatchPreExtractionGoldenAndTheResolver: (amount, kind, server tick).
    private static readonly (int Amount, FloatingCombatTextKind Kind, ulong Tick)[] PreExtractionGolden =
    [
        (50, FloatingCombatTextKind.Regular, 33),
        (46, FloatingCombatTextKind.Regular, 66),
        (75, FloatingCombatTextKind.Critical, 99),
        (35, FloatingCombatTextKind.Regular, 132),
        (33, FloatingCombatTextKind.Regular, 165),
        (25, FloatingCombatTextKind.Regular, 198),
        (39, FloatingCombatTextKind.Regular, 231),
        (46, FloatingCombatTextKind.Regular, 264),
        (38, FloatingCombatTextKind.Regular, 297),
        (39, FloatingCombatTextKind.Regular, 330),
        (75, FloatingCombatTextKind.Critical, 363),
        (26, FloatingCombatTextKind.Regular, 396),
    ];

    [Fact]
    public void RuntimePath_SeededRolls_MatchPreExtractionGoldenAndTheResolver()
    {
        SceneEventHub events = new();
        List<(int Amount, FloatingCombatTextKind Kind)> applied = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(
            damage => applied.Add((damage.Amount, damage.Kind)));
        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            simulationSeed: GoldenSeed);
        runtime.AttachRemoteSession(SessionA, "Player A");
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        runtime.Enemies.Clear();
        Vector3 position = runtime.Player.GetWorldPosition() + new Vector3(1.5f, 0f, 0f);
        EnemyActor enemy = CreateEnemy(events, position, maxHealth: 1_000_000);
        runtime.Enemies.Add(enemy);
        // A fresh combatant with the runtime player's (default) stats rolls the expected values.
        PlayerCombatant reference = new(() => Vector3.Zero);
        Assert.Equal(runtime.Player.Combatant.DerivedStats, reference.DerivedStats);

        int ticksPerAttack = (int)Math.Ceiling(
            runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds / SimulationClock.FixedDeltaSeconds) + 1;
        ulong tick = 0;
        List<(int Amount, FloatingCombatTextKind Kind, ulong Tick)> runtimeRolls = [];
        for (int hit = 1; hit <= PreExtractionGolden.Length; hit++)
        {
            for (int step = 0; step < ticksPerAttack; step++)
            {
                tick++;
                runtime.AdvanceAuthoritativeTick(
                    tick,
                    SimulationClock.FixedDeltaSeconds,
                    (PlayerCommandFrame?)null,
                    new PlayerSimulationTuning(0.35f, 2.5f, 6f, 150f, 5f),
                    allowGameplaySimulation: false);
            }

            runtime.Player.SetFacing(
                (enemy.Combatant.GetCombatAnchorWorldPosition() - runtime.Player.Camera.Position).Normalized());
            PlayerActionMessage message = new(SessionA, (ulong)hit * 7, new CombatUseIntent(Vector3.Zero, Vector3.UnitX));
            AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(message);

            Assert.True(result.Accepted, result.Rejection?.ToString());
            Assert.Equal(EnemyActorId, Assert.IsType<CombatResolutionResult>(result.Payload).TargetActorId);
            runtimeRolls.Add((applied[^1].Amount, applied[^1].Kind, result.ServerTick));

            PlayerAttackResolution expected = AuthoritativeCombatResolver.ResolveSeededAttack(
                GoldenSeed,
                reference,
                message,
                result.ServerTick,
                Seed.FromInt(EnemyActorId));
            Assert.Equal((expected.Damage, expected.FloatingTextKind), applied[^1]);
        }

        Assert.Equal(PreExtractionGolden, runtimeRolls);
    }

    [Fact]
    public void ResolveSeededAttack_InstanceAndStatic_AgreeAndArePureFunctionsOfTheInputs()
    {
        Fixture fixture = new(simulationSeed: 99UL);
        PlayerCombatant combatant = new(() => Vector3.Zero);
        PlayerActionMessage message = new(SessionA, 5, new CombatUseIntent(Vector3.Zero, Vector3.UnitX));

        PlayerAttackResolution instance = fixture.Resolver.ResolveSeededAttack(combatant, message, 12UL, Seed.FromInt(7));
        PlayerAttackResolution again = fixture.Resolver.ResolveSeededAttack(combatant, message, 12UL, Seed.FromInt(7));
        PlayerAttackResolution viaStatic = AuthoritativeCombatResolver.ResolveSeededAttack(
            99UL, combatant, message, 12UL, Seed.FromInt(7));

        Assert.Equal(instance, again);
        Assert.Equal(instance, viaStatic);
        Assert.Equal(99UL, fixture.Resolver.SimulationSeed);
    }

    [Fact]
    public void ProcessCombatUse_WithoutRuntime_HitsEnemyWithTheSeededRoll()
    {
        Fixture fixture = new(simulationSeed: 4242UL);
        EnemyActor enemy = fixture.AddEnemy(new Vector3(5f, 1f, 3f), maxHealth: 1_000_000);
        PlayerCombatant combatant = new(() => Vector3.Zero);
        PlayerActionMessage message = new(SessionA, 3, new CombatUseIntent(Vector3.Zero, Vector3.UnitX));
        PlayerAttackResolution expected = AuthoritativeCombatResolver.ResolveSeededAttack(
            4242UL, new PlayerCombatant(() => Vector3.Zero), message, 77UL, Seed.FromInt(EnemyActorId));

        AuthoritativeActionResult result = fixture.Resolver.ProcessCombatUse(
            message,
            fixture.RayAt(enemy.Combatant.GetCombatAnchorWorldPosition()),
            PlayerInteractionMode.Combat,
            combatant,
            tick: 77UL);

        Assert.True(result.Accepted);
        Assert.Equal(77UL, result.ServerTick);
        CombatResolutionResult payload = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.Equal(EnemyActorId, payload.TargetActorId);
        Assert.Equal(expected.Damage, payload.DamageApplied);
        Assert.False(payload.TargetDied);
        Assert.Equal([(expected.Damage, expected.FloatingTextKind)], fixture.Damage);
        Assert.Empty(fixture.Host.Calls);
    }

    [Fact]
    public void ProcessCombatUse_FatalEnemyHit_AwardsKillExperienceLevelsUpAndPersists()
    {
        Fixture fixture = new(simulationSeed: 1UL, formulas: CreateFormulas());
        EnemyActor enemy = fixture.AddEnemy(new Vector3(5f, 1f, 3f), maxHealth: 1, level: 100);
        PlayerCombatant combatant = new(() => Vector3.Zero, combatFormulaService: fixture.Formulas);
        fixture.Host.Combatants[SessionA] = combatant;

        AuthoritativeActionResult result = fixture.Resolver.ProcessCombatUse(
            new PlayerActionMessage(SessionA, 1, new CombatUseIntent(Vector3.Zero, Vector3.UnitX)),
            fixture.RayAt(enemy.Combatant.GetCombatAnchorWorldPosition()),
            PlayerInteractionMode.Combat,
            combatant,
            tick: 5UL);

        Assert.True(Assert.IsType<CombatResolutionResult>(result.Payload).TargetDied);
        // Reward 100 * 14 = 1400; level 1 needs 1000 + 23 = 1023, so level 2 with 377 left over.
        Assert.Equal(2, combatant.Level);
        Assert.Equal(377, combatant.Experience);
        // One level gained: five attribute points.
        Assert.Equal(5, combatant.StatPoints);
        Assert.Equal(
            // Wave I: the loot is granted after the delete, so a crash between them loses a drop, never duplicates it.
            ["PersistPlayerProgression session-a 2/377", $"DeletePersistedEnemy {EnemyActorId}", $"GrantLoot session-a Wolf {EnemyActorId} 5"],
            fixture.Host.Calls);
        Assert.Empty(fixture.Enemies.ActiveActors);
    }

    [Fact]
    public void AwardEnemyKillExperience_WithoutSessionCombatant_DoesNothing()
    {
        Fixture fixture = new(simulationSeed: 1UL, formulas: CreateFormulas());

        fixture.Resolver.AwardEnemyKillExperience(SessionA, enemyLevel: 5);

        Assert.Empty(fixture.Host.Calls);
    }

    [Fact]
    public void ProcessCombatUse_RejectsOutsideCombatModeAndOnCooldown()
    {
        Fixture fixture = new(simulationSeed: 1UL);
        PlayerCombatant combatant = new(() => Vector3.Zero);
        PlayerActionMessage message = new(SessionA, 1, new CombatUseIntent(Vector3.Zero, Vector3.UnitX));
        CombatUseIntent intent = new(Vector3.Zero, Vector3.UnitX);

        AuthoritativeActionResult building = fixture.Resolver.ProcessCombatUse(
            message, intent, PlayerInteractionMode.Building, combatant, tick: 3UL);
        AuthoritativeActionResult first = fixture.Resolver.ProcessCombatUse(
            message, intent, PlayerInteractionMode.Combat, combatant, tick: 3UL);
        AuthoritativeActionResult cooldown = fixture.Resolver.ProcessCombatUse(
            message, intent, PlayerInteractionMode.Combat, combatant, tick: 3UL);

        Assert.False(building.Accepted);
        Assert.Equal("Combat action rejected because the authoritative mode is not Combat.", building.Rejection?.Reason);
        Assert.True(first.Accepted);
        Assert.Null(Assert.IsType<CombatResolutionResult>(first.Payload).TargetActorId);
        Assert.False(cooldown.Accepted);
        Assert.Equal("Combat action rejected because attack is on cooldown.", cooldown.Rejection?.Reason);
        Assert.Equal(3UL, cooldown.ServerTick);
    }

    [Fact]
    public void ProcessCombatUse_FatalCubeHit_FracturesAndPublishesTheCellPieces()
    {
        Fixture fixture = new(simulationSeed: 1UL);
        GeometryCellKey cell = new(5, 1, 3);
        Cube cube = Assert.IsType<Cube>(fixture.Map.TryAddCube(new Vector3(5f, 1f, 3f), withPhysics: false));
        WorldObjectRecord record = fixture.WorldObjects.RegisterCube(cube, fixture.Map, maxHealth: 1);
        List<CubeFracturedEvent> fractures = [];
        using IDisposable subscription = fixture.Events.Subscribe<CubeFracturedEvent>(fractures.Add);

        AuthoritativeActionResult result = fixture.Resolver.ProcessCombatUse(
            new PlayerActionMessage(SessionA, 1, new CombatUseIntent(Vector3.Zero, Vector3.UnitX)),
            new CombatUseIntent(new Vector3(5.5f, 1.5f, 6f), -Vector3.UnitZ),
            PlayerInteractionMode.Combat,
            new PlayerCombatant(() => Vector3.Zero),
            tick: 9UL);

        CombatResolutionResult payload = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.Equal(record.Actor.Id, payload.TargetActorId);
        Assert.True(payload.TargetDied);
        CubeFracturedEvent fracture = Assert.Single(fractures);
        Assert.Equal(new Vector3(5f, 1f, 3f), fracture.CellPosition);
        Assert.Equal(
            CubeFracturePattern.CreateFirstPassRubble(cell).Where(GeometryFracturePiece.IsValid),
            fracture.Pieces);
        Assert.Equal(
            [
                $"RepairCubeRecord {record.Actor.Id}",
                $"DeletePersistedPlacedCube {record.Actor.Id}",
                $"PersistFracturePieces {record.Actor.Id} {cell}"
            ],
            fixture.Host.Calls);
        Assert.False(fixture.WorldObjects.TryGetByActorId(record.Actor.Id, out _));
    }

    [Fact]
    public void CaptureCellFracturePieces_MatchesTheCaptureAllThenFilterPath_WithOneThousandRubbleCells()
    {
        Map map = CreateRubbleMap(out List<GeometryCellKey> cells);
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> scratch = [];

        foreach (GeometryCellKey cell in cells)
        {
            GeometryFracturePiece[] old = CaptureByFilteringEverything(map, cell);
            GeometryFracturePiece[] current = AuthoritativeCombatResolver.CaptureCellFracturePieces(map, cell, scratch);

            Assert.NotEmpty(old);
            Assert.Equal(old, current);
            Assert.Empty(scratch);
        }

        Assert.Empty(AuthoritativeCombatResolver.CaptureCellFracturePieces(map, new GeometryCellKey(0, 5, 0), scratch));
    }

    // Measures the old and the new piece capture for one fracture with 1000 rubble cells on the
    // map. Reports the numbers; asserts only the direction, so it does not flake on a busy machine.
    [Fact]
    public void CaptureCellFracturePieces_IsCheaperThanCaptureAllThenFilter_WithOneThousandRubbleCells()
    {
        Map map = CreateRubbleMap(out List<GeometryCellKey> cells);
        GeometryCellKey target = cells[cells.Count / 2];
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> scratch = [];
        const int iterations = 200;
        for (int warm = 0; warm < 20; warm++)
        {
            _ = CaptureByFilteringEverything(map, target);
            _ = AuthoritativeCombatResolver.CaptureCellFracturePieces(map, target, scratch);
        }

        (double oldMicros, long oldBytes) = Measure(iterations, () => CaptureByFilteringEverything(map, target));
        (double newMicros, long newBytes) = Measure(
            iterations,
            () => AuthoritativeCombatResolver.CaptureCellFracturePieces(map, target, scratch));

        output.WriteLine(
            $"FRACTURE-CAPTURE 1000 rubble cells: old {oldMicros:F2} us / {oldBytes} B per fracture, " +
            $"new {newMicros:F2} us / {newBytes} B per fracture");
        Assert.True(newBytes < oldBytes, $"new {newBytes} B, old {oldBytes} B");
        Assert.True(newMicros < oldMicros, $"new {newMicros} us, old {oldMicros} us");
    }

    // The pre-extraction code path, kept here as the reference the new capture must reproduce.
    private static GeometryFracturePiece[] CaptureByFilteringEverything(Map map, GeometryCellKey cell)
    {
        return map.CaptureFracturePieces()
            .Where(piece => piece.Cell == cell)
            .Select(piece => piece.Piece)
            .Where(GeometryFracturePiece.IsValid)
            .ToArray();
    }

    private static (double MicrosPerCall, long BytesPerCall) Measure(int iterations, Func<GeometryFracturePiece[]> capture)
    {
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        int sink = 0;
        for (int index = 0; index < iterations; index++)
        {
            sink += capture().Length;
        }

        stopwatch.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        Assert.True(sink > 0);
        return (stopwatch.Elapsed.TotalMicroseconds / iterations, bytes / iterations);
    }

    private static Map CreateRubbleMap(out List<GeometryCellKey> cells)
    {
        Map map = new(physics: null, generateStartupTerrain: false, width: 40, height: 8, depth: 40, headless: true);
        cells = [];
        for (int x = 0; x < 40 && cells.Count < 1000; x++)
        {
            for (int z = 0; z < 40 && cells.Count < 1000; z++)
            {
                GeometryCellKey cell = new(x, 1 + ((x + z) % 3), z);
                Assert.True(map.RestoreFracturePieces(
                    cell,
                    GeometryMaterialId.Stone,
                    CubeFracturePattern.CreateFirstPassRubble(cell),
                    withPhysics: false));
                cells.Add(cell);
            }
        }

        Assert.Equal(1000, cells.Count);
        return map;
    }

    private static EnemyActor CreateEnemy(SceneEventHub events, Vector3 position, int maxHealth, int level = 1)
    {
        EnemyCombatant combatant = new(
            () => position,
            events,
            actorId: EnemyActorId,
            maxHealth: maxHealth,
            attackDamage: 1,
            attackIntervalSeconds: 1f);
        return new EnemyActor(
            combatant,
            position,
            aggroRange: 0f,
            attackRange: 0f,
            moveSpeed: 0f,
            displayName: "Wolf",
            level: level);
    }

    private static CombatFormulaService CreateFormulas()
    {
        return CombatFormulaService.CreateForDefinitions(
        [
            new CombatFormulaDefinition("player.min_damage", "5 + strength * 2", "test", true, DateTime.UtcNow),
            new CombatFormulaDefinition("player.max_damage", "10 + strength * 4", "test", true, DateTime.UtcNow),
            new CombatFormulaDefinition("player.max_health", "50 + vitality * 5", "test", true, DateTime.UtcNow),
            new CombatFormulaDefinition("player.critical_damage", "max_damage * 1.5", "test", true, DateTime.UtcNow),
            new CombatFormulaDefinition("player.critical_chance", "0.15", "test", true, DateTime.UtcNow),
            new CombatFormulaDefinition("player.attack_interval_seconds", "1.0 / attack_speed", "test", true, DateTime.UtcNow),
            new CombatFormulaDefinition("player.experience_to_next_level", "1000 + level * 23", "test", true, DateTime.UtcNow),
            new CombatFormulaDefinition("enemy.experience_reward", "enemy_level * 14", "test", true, DateTime.UtcNow),
            ..Dream.Tests.Support.WaveIFormulas.Definitions()
        ]);
    }

    private sealed class Fixture
    {
        public Fixture(ulong simulationSeed, CombatFormulaService? formulas = null)
        {
            Formulas = formulas ?? CombatFormulaService.Default;
            Events.Subscribe<DamageAppliedEvent>(damage => Damage.Add((damage.Amount, damage.Kind)));
            Resolver = new AuthoritativeCombatResolver(
                simulationSeed,
                () => Map,
                Enemies,
                WorldObjects,
                Events,
                Formulas,
                Host);
        }

        public Map Map { get; } = new(physics: null, generateStartupTerrain: false, width: 10, height: 8, depth: 10, headless: true);

        public EnemyRegistry Enemies { get; } = new();

        public WorldObjectRegistry WorldObjects { get; } = new();

        public SceneEventHub Events { get; } = new();

        public CombatFormulaService Formulas { get; }

        public RecordingHost Host { get; } = new();

        public AuthoritativeCombatResolver Resolver { get; }

        public List<(int Amount, FloatingCombatTextKind Kind)> Damage { get; } = [];

        public EnemyActor AddEnemy(Vector3 position, int maxHealth, int level = 1)
        {
            EnemyActor enemy = CreateEnemy(Events, position, maxHealth, level);
            Enemies.Add(enemy);
            return enemy;
        }

        public CombatUseIntent RayAt(Vector3 anchor)
        {
            return new CombatUseIntent(anchor + new Vector3(0f, 0f, 2f), -Vector3.UnitZ);
        }
    }

    private sealed class RecordingHost : IAuthoritativeCombatHost
    {
        public Dictionary<PlayerSessionId, PlayerCombatant> Combatants { get; } = [];

        public List<string> Calls { get; } = [];

        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            bool found = Combatants.TryGetValue(sessionId, out PlayerCombatant? value);
            combatant = value;
            return found;
        }

        public void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant)
            => Calls.Add($"PersistPlayerProgression {sessionId.Value} {combatant.Level}/{combatant.Experience}");

        public void DeletePersistedEnemy(int enemyId) => Calls.Add($"DeletePersistedEnemy {enemyId}");

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick)
            => Calls.Add($"GrantLoot {killer.Value} {enemyKind} {enemyActorId} {tick}");

        public WorldObjectRecord RepairCubeRecord(WorldObjectRecord record)
        {
            Calls.Add($"RepairCubeRecord {record.Actor.Id}");
            return record;
        }

        public void UpdatePersistedCubeHealth(WorldObjectRecord record)
            => Calls.Add($"UpdatePersistedCubeHealth {record.Actor.Id} {record.Health.CurrentHealth}");

        public void DeletePersistedPlacedCube(int cubeId) => Calls.Add($"DeletePersistedPlacedCube {cubeId}");

        public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell)
            => Calls.Add($"PersistFracturePieces {record.Actor.Id} {cell}");

        public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell)
            => Calls.Add($"PersistTerrainCellRemoved {record.Actor.Id} {cell}");
    }
}
