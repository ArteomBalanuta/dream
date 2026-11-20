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
// These tests drive the resolver with no runtime, pin the runtime path's seeded rolls to a recorded
// array (see SeededRollsGolden: re-recorded once the attack cadence moved, so it is a determinism pin
// rather than pre-extraction evidence), and check the single-cell fracture capture against the old
// capture-everything-then-filter path.
public class AuthoritativeCombatResolverTests(ITestOutputHelper output)
{
    private const ulong GoldenSeed = 20261028UL;
    private const int EnemyActorId = 4100;
    private static readonly PlayerSessionId SessionA = new("session-a");

    // (amount, kind, server tick) for the hit sequence in
    // RuntimePath_SeededRolls_MatchTheRecordedRollsAndTheResolver.
    //
    // Re-recorded on 2026-11-04. The original array was recorded on 91449a7, before combat resolution
    // moved out of WorldSessionRuntime, and it no longer holds: 3fc2a4f gave Agility +0.01 attack speed
    // per point, so a default character's effective attack speed went 2.0 -> 2.1, the attack interval
    // 0.5 s -> 1 / 2.1 s and the cadence 33 ticks -> 32. Every hit therefore lands on a different server
    // tick, and the tick seeds the roll, so all twelve amounts change with it.
    //
    // Note for the next reader: this array no longer proves the extraction was faithful - that evidence
    // could not be preserved, because the cadence genuinely moved. What still proves it is the per-hit
    // cross-check against the live AuthoritativeCombatResolver.ResolveSeededAttack inside the loop, which
    // passed throughout. Treat this array as a determinism pin only: if it moves again without a
    // deliberate change to the cadence, the damage roll or the seeding, that is a regression.
    private static readonly (int Amount, FloatingCombatTextKind Kind, ulong Tick)[] SeededRollsGolden =
    [
        (44, FloatingCombatTextKind.Regular, 32),
        (29, FloatingCombatTextKind.Regular, 64),
        (40, FloatingCombatTextKind.Regular, 96),
        (46, FloatingCombatTextKind.Regular, 128),
        (40, FloatingCombatTextKind.Regular, 160),
        (30, FloatingCombatTextKind.Regular, 192),
        (75, FloatingCombatTextKind.Critical, 224),
        (43, FloatingCombatTextKind.Regular, 256),
        (38, FloatingCombatTextKind.Regular, 288),
        (48, FloatingCombatTextKind.Regular, 320),
        (75, FloatingCombatTextKind.Critical, 352),
        (39, FloatingCombatTextKind.Regular, 384),
    ];

    [Fact]
    public void RuntimePath_SeededRolls_MatchTheRecordedRollsAndTheResolver()
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
        for (int hit = 1; hit <= SeededRollsGolden.Length; hit++)
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

        output.WriteLine(string.Join(" ", runtimeRolls.Select(r => $"({r.Amount}, {r.Kind}, {r.Tick})")));
        Assert.Equal(SeededRollsGolden, runtimeRolls);
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

    // The reach gate is horizontal anchor distance <= reach + tolerance. Both sides of the line are asserted, so a
    // gate that stopped measuring (always true, or always false) fails one of them.
    [Theory]
    [InlineData(1.5f, 1.5f, true)]
    [InlineData(1.5f, 1.5f + WeaponReach.ServerToleranceMetres - 0.01f, true)]
    [InlineData(1.5f, 1.5f + WeaponReach.ServerToleranceMetres + 0.05f, false)]
    [InlineData(1.5f, 2.0f, false)]
    [InlineData(14f, 12f, true)]
    [InlineData(14f, 14f + WeaponReach.ServerToleranceMetres + 0.05f, false)]
    [InlineData(WeaponReach.UnarmedMetres, 1.4f, true)]
    [InlineData(WeaponReach.UnarmedMetres, 2.0f, false)]
    public void ProcessCombatUse_HitsAnEnemyOnlyWithinTheWeaponsReachPlusTolerance(float reach, float distance, bool expectHit)
    {
        Fixture fixture = new(simulationSeed: 4242UL);
        fixture.Host.Reach = reach;
        EnemyActor enemy = fixture.AddEnemy(new Vector3(5f, 1f, 3f), maxHealth: 1_000_000);
        PlayerCombatant combatant = new(() => Vector3.Zero);

        AuthoritativeActionResult result = fixture.Resolver.ProcessCombatUse(
            new PlayerActionMessage(SessionA, 3, new CombatUseIntent(Vector3.Zero, Vector3.UnitX)),
            fixture.RayAt(enemy.Combatant.GetCombatAnchorWorldPosition(), distance),
            PlayerInteractionMode.Combat,
            combatant,
            tick: 77UL);

        // A swing out of reach is accepted like any miss: no damage, no target, the cooldown is spent.
        Assert.True(result.Accepted);
        CombatResolutionResult payload = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.Equal(expectHit, payload.TargetActorId == EnemyActorId);
        Assert.Equal(expectHit, fixture.Damage.Count == 1);
        Assert.Equal(expectHit, enemy.Combatant.Health.CurrentHealth < 1_000_000);
    }

    // The same gate for a player victim: the PvP clause of ProcessCombatUse measures the horizontal anchor
    // distance too, so deleting it lets the swing land from beyond reach and fails the out-of-reach case.
    [Theory]
    [InlineData(1.5f, 1.5f, true)]
    [InlineData(1.5f, 1.5f + WeaponReach.ServerToleranceMetres - 0.01f, true)]
    [InlineData(1.5f, 1.5f + WeaponReach.ServerToleranceMetres + 0.05f, false)]
    [InlineData(14f, 14f + WeaponReach.ServerToleranceMetres - 0.01f, true)]
    [InlineData(14f, 14f + WeaponReach.ServerToleranceMetres + 0.05f, false)]
    public void ProcessCombatUse_HitsAPlayerOnlyWithinTheWeaponsReachPlusTolerance(float reach, float distance, bool expectHit)
    {
        Fixture fixture = new(simulationSeed: 4242UL);
        fixture.Host.Reach = reach;
        fixture.Host.Pvp = true;
        PlayerCombatant victim = new(() => new Vector3(5f, 1f, 3f), actorId: 900);
        victim.Health.SetAuthoritativeState(10_000, 10_000);
        fixture.Host.PvpVictims.Add(victim);
        PlayerCombatant attacker = new(() => Vector3.Zero);

        AuthoritativeActionResult result = fixture.Resolver.ProcessCombatUse(
            new PlayerActionMessage(SessionA, 3, new CombatUseIntent(Vector3.Zero, Vector3.UnitX)),
            fixture.RayAt(victim.Actor.GetWorldAnchor(), distance),
            PlayerInteractionMode.Combat,
            attacker,
            tick: 77UL);

        Assert.True(result.Accepted);
        CombatResolutionResult payload = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.Equal(expectHit, payload.TargetActorId == 900);
        Assert.Equal(expectHit, victim.Health.CurrentHealth < 10_000);
    }

    // The nearest enemy ALONG THE RAY is not always the nearest horizontally: an enemy off to the side of the ray
    // is near along it but farther from the attacker's feet. Gating after the pick lets it shadow an enemy that is
    // straight ahead and in reach; the gate is part of the selection, so the one in reach takes the swing.
    // Regression: the player's swing ray is flat (the isometric cursor aim sets a yaw-only facing) and starts at the
    // eye, 0.85 m above the feet. An enemy standing one block higher (or lower) has its anchor 0.9 m off that ray
    // in height alone, which the 0.9 m hit radius took as a miss however well the player aimed, so the left-click
    // attack never landed on it. The ray test must measure the lateral miss on the ground plane.
    [Theory]
    [InlineData(1.0f, 1.3f, 0f, true)]   // level ground, dead ahead
    [InlineData(2.0f, 1.3f, 0f, true)]   // one block up: the case that failed
    [InlineData(0.0f, 1.3f, 0f, true)]   // one block down
    [InlineData(2.0f, 1.3f, 0.5f, true)] // one block up, aimed a little off
    [InlineData(2.0f, 1.3f, 1.0f, false)] // one block up, aimed well off: still a miss
    [InlineData(4.0f, 1.3f, 0f, false)]  // three blocks up: out of any melee reach
    public void ProcessCombatUse_AFlatEyeHeightRayHitsAnEnemyOnAnotherFloorLevel(float feetY, float distance, float lateral, bool expectHit)
    {
        Fixture fixture = new(simulationSeed: 4242UL);
        fixture.Host.Reach = WeaponReach.UnarmedMetres;
        EnemyActor enemy = fixture.AddEnemy(new Vector3(5f + distance, feetY, 5f + lateral), maxHealth: 1_000_000);
        Vector3 eye = new(5f, 1.85f, 5f);

        AuthoritativeActionResult result = fixture.Resolver.ProcessCombatUse(
            new PlayerActionMessage(SessionA, 3, new CombatUseIntent(eye, Vector3.UnitX)),
            new CombatUseIntent(eye, Vector3.UnitX),
            PlayerInteractionMode.Combat,
            new PlayerCombatant(() => Vector3.Zero),
            tick: 77UL);

        Assert.True(result.Accepted);
        Assert.Equal(expectHit, Assert.IsType<CombatResolutionResult>(result.Payload).TargetActorId == EnemyActorId);
        Assert.Equal(expectHit, enemy.Combatant.Health.CurrentHealth < 1_000_000);
    }

    [Fact]
    public void ProcessCombatUse_AnEnemyOutOfReachNearerAlongTheRayDoesNotShadowOneInReach()
    {
        const int InReachId = 4101;
        const int OutOfReachId = 4102;
        const float AheadMetres = 1.2f;
        const float SideMetres = 0.85f; // inside the ray's hit radius
        const float AlongMetres = 1.0f; // nearer along the ray than AheadMetres
        float sideHorizontal = MathF.Sqrt((AlongMetres * AlongMetres) + (SideMetres * SideMetres));
        Assert.True(AlongMetres < AheadMetres && AheadMetres < sideHorizontal);

        Fixture fixture = new(simulationSeed: 4242UL);
        // Accepted reach halfway between the two horizontal distances.
        fixture.Host.Reach = ((AheadMetres + sideHorizontal) / 2f) - WeaponReach.ServerToleranceMetres;
        EnemyActor ahead = fixture.AddEnemy(new Vector3(5f, 1f, 3f), maxHealth: 1_000_000, actorId: InReachId);
        Vector3 aheadAnchor = ahead.Combatant.GetCombatAnchorWorldPosition();
        EnemyActor side = fixture.AddEnemy(new Vector3(5f + SideMetres, 1f, 3f + (AheadMetres - AlongMetres)), maxHealth: 1_000_000, actorId: OutOfReachId);

        AuthoritativeActionResult result = fixture.Resolver.ProcessCombatUse(
            new PlayerActionMessage(SessionA, 3, new CombatUseIntent(Vector3.Zero, Vector3.UnitX)),
            fixture.RayAt(aheadAnchor, AheadMetres),
            PlayerInteractionMode.Combat,
            new PlayerCombatant(() => Vector3.Zero),
            tick: 77UL);

        Assert.Equal(InReachId, Assert.IsType<CombatResolutionResult>(result.Payload).TargetActorId);
        Assert.True(ahead.Combatant.Health.CurrentHealth < 1_000_000);
        Assert.Equal(1_000_000, side.Combatant.Health.CurrentHealth);
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
        // One level gained: five stat points (derived from the level, nothing stored).
        Assert.Equal(5, PlayerStatRules.PointsAvailable(combatant.Level, combatant.BaseStats));
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
        // The record is repaired twice: once for the build-protection gate, once on entering the hit. The
        // second call finds the identity matching and returns the record unchanged, so it is harmless.
        Assert.Equal(
            [
                $"RepairCubeRecord {record.Actor.Id}",
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

    private static EnemyActor CreateEnemy(SceneEventHub events, Vector3 position, int maxHealth, int level = 1, int actorId = EnemyActorId)
    {
        EnemyCombatant combatant = new(
            () => position,
            events,
            actorId: actorId,
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

        public EnemyActor AddEnemy(Vector3 position, int maxHealth, int level = 1, int actorId = EnemyActorId)
        {
            EnemyActor enemy = CreateEnemy(Events, position, maxHealth, level, actorId);
            Enemies.Add(enemy);
            return enemy;
        }

        // A ray that starts the given horizontal distance from the anchor and looks at it; inside the unarmed
        // reach by default.
        public CombatUseIntent RayAt(Vector3 anchor, float distance = 1f)
        {
            return new CombatUseIntent(anchor + new Vector3(0f, 0f, distance), -Vector3.UnitZ);
        }
    }

    private sealed class RecordingHost : IAuthoritativeCombatHost
    {
        public Dictionary<PlayerSessionId, PlayerCombatant> Combatants { get; } = [];

        public List<string> Calls { get; } = [];

        public float Reach { get; set; } = WeaponReach.UnarmedMetres;

        public float ResolveAttackReach(PlayerSessionId attacker) => Reach;

        public bool Pvp { get; set; }

        public List<PlayerCombatant> PvpVictims { get; } = [];

        public CombatTargetPolicy TargetPolicy => new(Pvp);

        public void FillPvpTargets(PlayerSessionId attacker, List<CombatTargetCandidate> into)
        {
            foreach (PlayerCombatant victim in PvpVictims)
            {
                into.Add(new CombatTargetCandidate(victim.Actor, victim.Actor.GetWorldAnchor(), Priority: 0));
            }
        }

        public PlayerCombatant? ResolvePvpVictim(int actorId) => PvpVictims.Find(victim => victim.Actor.Id == actorId);

        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            bool found = Combatants.TryGetValue(sessionId, out PlayerCombatant? value);
            combatant = value;
            return found;
        }

        public void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant)
            => Calls.Add($"PersistPlayerProgression {sessionId.Value} {combatant.Level}/{combatant.Experience}");

        public void DeletePersistedEnemy(int enemyId) => Calls.Add($"DeletePersistedEnemy {enemyId}");

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick, OpenTK.Mathematics.Vector3 deathPosition)
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
