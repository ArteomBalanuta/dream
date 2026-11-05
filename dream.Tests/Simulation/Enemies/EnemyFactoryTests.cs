using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Tests.Simulation.Runtime;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Enemies;

// Wave I C0b: EnemyFactory builds an enemy from a template (spawn) or a row (restore) with its
// stats on a StatSheet and damage, interval and health derived through the enemy.* formulas.
public sealed class EnemyFactoryTests
{
    private static readonly SceneEventHub Events = new();

    private static EnemyTemplate Dire(float? interval = null) => new(
        "DireWolf", "Dire Wolf", Level: 3, Strength: 16, Vitality: 30, AttackSpeed: 1.0f, interval,
        AggroRange: 12f, AttackRange: 1.35f, MoveSpeed: 3.2f, "enemy.dire-wolf", "dire-wolf", EnemySpawnRole.NightExtra);

    [Fact]
    public void ADireSpawn_DerivesDamageHealthAndInterval_FromItsTemplateStats()
    {
        EnemyActor dire = EnemyFactory.Spawn(Dire(), new Vector3(1f, 1f, 1f), 2100, Events, CombatFormulaService.Default);

        Assert.Equal(18, dire.Combatant.AttackDamage);
        Assert.Equal(200, dire.Combatant.Health.MaxHealth);
        Assert.Equal(1.0f, dire.Combatant.AttackIntervalSeconds);
        Assert.Equal(3, dire.Level);
        Assert.Equal("enemy.dire-wolf", dire.VisualKey);
        Assert.Equal("DireWolf", dire.EnemyKind);
        Assert.Equal(12f, dire.AggroRange);
        Assert.Equal(3.2f, dire.MoveSpeed);

        ref readonly StatBlock stats = ref dire.Combatant.StatSheet.Base;
        Assert.Equal(16f, stats[StatId.Strength]);
        Assert.Equal(EnemyFactory.DefaultAgility, (int)stats[StatId.Agility]);
        Assert.Equal(30f, stats[StatId.Vitality]);
        Assert.Equal(EnemyFactory.DefaultEnergy, (int)stats[StatId.Energy]);
        Assert.Equal(1.0f, stats[StatId.AttackSpeed]);
    }

    [Fact]
    public void AnExplicitTemplateInterval_ReplacesTheFormula_OnSpawnOnly()
    {
        EnemyActor spawned = EnemyFactory.Spawn(Dire(interval: 2.5f), Vector3.Zero, 2100, Events, CombatFormulaService.Default);
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(16, 30, 1.0f, 200, 200, "DireWolf", 3);
        EnemyActor restored = EnemyFactory.Restore(row, Dire(interval: 2.5f), 2101, Events, CombatFormulaService.Default);

        Assert.Equal(2.5f, spawned.Combatant.AttackIntervalSeconds);
        Assert.Equal(1.0f, restored.Combatant.AttackIntervalSeconds);
    }

    [Fact]
    public void ARestore_ReadsStatsAndHealthFromTheRow_NotTheTemplate()
    {
        // A Dire row stored under an older balance: the template now says 16 / 30 but the row keeps its own.
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: 20, vitality: 40, attackSpeed: 2.0f, currentHealth: 90, maxHealth: 250, "DireWolf", level: 3);

        EnemyActor restored = EnemyFactory.Restore(row, Dire(), 2100, Events, CombatFormulaService.Default);

        Assert.Equal(22, restored.Combatant.AttackDamage);
        Assert.Equal(0.5f, restored.Combatant.AttackIntervalSeconds);
        Assert.Equal(250, restored.Combatant.Health.MaxHealth);
        Assert.Equal(90, restored.Combatant.Health.CurrentHealth);
        Assert.Equal(3, restored.Level);
        Assert.Equal("DireWolf", restored.EnemyKind);
        Assert.Equal("enemy.dire-wolf", restored.VisualKey);
        Assert.Equal(row.Facing, restored.Forward);
        Assert.Equal(row.Position, restored.Position);
    }

    [Fact]
    public void ARestore_OfARowAboveTheContentMaximumLevel_UsesTheClampedLevelForXpAndTheWire()
    {
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: 16, vitality: 30, attackSpeed: 1.0f, currentHealth: 200, maxHealth: 200, "DireWolf", level: 5000);

        EnemyActor restored = EnemyFactory.Restore(row, Dire(), 2100, Events, CombatFormulaService.Default);

        Assert.Equal(ContentCatalog.MaxEnemyLevel, restored.Level);
        Assert.Equal(1000, ContentCatalog.MaxEnemyLevel);
        // Kill XP is the reward for the actor's level (AuthoritativeCombatResolver.AwardEnemyKillExperience is
        // handed it), so the clamp must change the reward: the stored level would pay more.
        CombatFormulaService formulas = CombatFormulaService.Default;
        int rewardAtStoredLevel = formulas.EvaluateEnemyExperienceReward(5000, 1);
        int rewardAtActorLevel = formulas.EvaluateEnemyExperienceReward(restored.Level, 1);
        Assert.True(rewardAtActorLevel > 0);
        Assert.True(rewardAtActorLevel < rewardAtStoredLevel, $"{rewardAtActorLevel} vs {rewardAtStoredLevel}");

        // The wire value (WorldSessionRuntime.Replication) through the real codec, then the client's applier.
        ActorSnapshot wire = new(
            restored.Combatant.Actor.Id, CreatureKind.Enemy, null, restored.Position, restored.Forward,
            restored.Combatant.Health.CurrentHealth, restored.Combatant.Health.MaxHealth, true,
            EnemyLevel: restored.Level == 1 ? 0 : restored.Level);
        ServerSnapshotMessage message = new(new ServerSnapshotEnvelope(
            11, new Dictionary<string, ulong>(), [wire], PlayerInteractionMode.Combat, null, [], [], WorldChecksum: 0, WorldChecksumSequence: 0));
        TransportJsonCodec codec = new();
        DecodedProtocolMessage decoded = codec.Decode(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));
        ServerSnapshotMessage received = Assert.IsType<ServerSnapshotMessage>(decoded.Message);
        ActorSnapshot decodedActor = Assert.Single(received.Snapshot.Actors);
        Assert.Equal(1000, decodedActor.EnemyLevel);

        EnemyRegistry clientEnemies = new();
        RemoteEnemyStateApplier.ApplyActorSnapshots(clientEnemies, [decodedActor]);
        Assert.Equal(1000, Assert.Single(clientEnemies.ActiveActors).Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ARestore_OfARowBelowLevelOne_UsesLevelOne_ForTheActorAndTheWire(int storedLevel)
    {
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: 16, vitality: 30, attackSpeed: 1.0f, currentHealth: 200, maxHealth: 200, "DireWolf", level: storedLevel);

        EnemyActor restored = EnemyFactory.Restore(row, Dire(), 2100, Events, CombatFormulaService.Default);

        Assert.Equal(1, restored.Level);
    }

    // Kills a restored enemy through the real path (ProcessAuthoritativeAction -> AuthoritativeCombatResolver ->
    // AwardEnemyKillExperience) and returns what the killer ended with.
    private static (int Level, int Experience) KillerAfterKillingARowOfLevel(int storedLevel)
    {
        PlayerSessionId killer = new("xp-killer");
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(killer, killer.Value);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 3);
        WaveIRuntimeKit.EnterCombat(runtime, killer);
        runtime.Enemies.Clear();
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: 16, vitality: 30, attackSpeed: 1.0f, currentHealth: 200, maxHealth: 200, "DireWolf", level: storedLevel);
        runtime.Enemies.Add(EnemyFactory.Restore(row, Dire(), 2100, Events, CombatFormulaService.Default));

        AuthoritativeActionResult result = WaveIRuntimeKit.HitWolf(runtime, killer, actionSequence: 1);

        Assert.True(Assert.IsType<CombatResolutionResult>(result.Payload).TargetDied);
        PlayerCombatant combatant = WaveIRuntimeKit.Target(runtime, killer.Value).Combatant;
        return (combatant.Level, combatant.Experience);
    }

    [Theory]
    [InlineData(5000, 1000)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void AKillOfARestoredRow_PaysTheClampedLevel_ThroughTheRealCombatPath(int storedLevel, int clampedLevel)
    {
        (int Level, int Experience) paid = KillerAfterKillingARowOfLevel(storedLevel);
        (int Level, int Experience) expected = KillerAfterKillingARowOfLevel(clampedLevel);

        Assert.Equal(expected, paid);
        // The two ends differ, so the comparison above can fail.
        (int Level, int Experience) other = KillerAfterKillingARowOfLevel(clampedLevel == 1 ? 1000 : 1);
        Assert.NotEqual(expected, other);
    }

    [Fact]
    public void ARestore_OfAHostileRow_ClampsItsStats_AndNeverThrows()
    {
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: int.MaxValue, vitality: -5, attackSpeed: float.NaN, currentHealth: 5, maxHealth: 10);

        EnemyActor restored = EnemyFactory.Restore(row, ContentCatalog.Shipped.Wolf, 2100, Events, CombatFormulaService.Default);

        // The ceiling is the enemy one (StatRules.MaxEnemyBase), not the player's MaxStrength.
        Assert.Equal(StatRules.MaxEnemyBase, restored.Combatant.StatSheet.Base[StatId.Strength]);
        Assert.Equal(1f, restored.Combatant.StatSheet.Base[StatId.Vitality]);
        Assert.Equal(0.5f, restored.Combatant.StatSheet.Base[StatId.AttackSpeed]);
        Assert.Equal(52, restored.Combatant.AttackDamage);
        Assert.Equal(2.0f, restored.Combatant.AttackIntervalSeconds);
        Assert.Equal(10, restored.Combatant.Health.MaxHealth);
    }

    // The enemy base ceiling is a damage ceiling, because `enemy.attack_damage = strength + 2`. It must
    // stay far below the player stat ceiling (10000): clamping an enemy
    // with the player cap turns a tampered or corrupt row's bite from 52 into 10002, which one-shots any
    // player through a base max health of 100 regardless of gear. The shipped templates top out at
    // Strength 40 (Wolf 10, DireWolf 16, AshenProwler 24, GraveHowler 40), so 50 keeps real headroom.
    // Do not widen this to the player ceiling, and do not re-pin it upward to make a failure go away.
    [Fact]
    public void AHostileStrengthRow_ClampsToTheEnemyCeiling_NotThePlayerCeiling_SoTheWorstBiteIs52()
    {
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: int.MaxValue, vitality: int.MaxValue, attackSpeed: 1.0f, currentHealth: 5, maxHealth: 10);

        EnemyActor restored = EnemyFactory.Restore(row, ContentCatalog.Shipped.Wolf, 2101, Events, CombatFormulaService.Default);

        Assert.Equal(50f, StatRules.MaxEnemyBase);
        Assert.True(StatRules.MaxEnemyBase < PlayerCombatStats.MaxStrength, "the enemy ceiling must stay under the player ceiling");
        Assert.Equal(StatRules.MaxEnemyBase, restored.Combatant.StatSheet.Base[StatId.Strength]);
        Assert.Equal(StatRules.MaxEnemyBase, restored.Combatant.StatSheet.Base[StatId.Vitality]);
        // strength + 2 = 50 + 2.
        Assert.Equal(52, restored.Combatant.AttackDamage);
        // A fresh player has 100 max health, so the worst row survives a bite with room to spare.
        Assert.True(restored.Combatant.AttackDamage < 100);
    }

    // The flip side of the ceiling: no shipped template may sit above it, or EnemyFactory would clamp a
    // legitimate enemy down without a word. Today the maxima are Strength 40 and Vitality 50, and
    // Vitality is exactly at the ceiling - this is the test that fails if either is raised past it.
    [Fact]
    public void EveryShippedEnemyTemplate_FitsUnderTheEnemyCeiling_SoNoneIsSilentlyClamped()
    {
        Assert.NotEmpty(ContentCatalog.Shipped.Enemies);
        foreach (EnemyTemplate template in ContentCatalog.Shipped.Enemies)
        {
            Assert.True(template.Strength <= StatRules.MaxEnemyBase, $"{template.Key} strength {template.Strength} is above the enemy ceiling {StatRules.MaxEnemyBase}");
            Assert.True(template.Vitality <= StatRules.MaxEnemyBase, $"{template.Key} vitality {template.Vitality} is above the enemy ceiling {StatRules.MaxEnemyBase}");
        }
    }

    [Fact]
    public void TheDefaultVisual_IsNullOnTheActor_AndAScriptedEnemyKeepsTodaysRowStats()
    {
        EnemyActor wolf = EnemyFactory.Spawn(ContentCatalog.Shipped.Wolf, Vector3.Zero, 2000, Events, CombatFormulaService.Default);
        EnemyCombatant scripted = new(() => Vector3.Zero, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f);

        Assert.Null(wolf.VisualKey);
        Assert.Equal(10f, scripted.StatSheet.Base[StatId.Strength]);
        Assert.Equal(10f, scripted.StatSheet.Base[StatId.Agility]);
        Assert.Equal(10f, scripted.StatSheet.Base[StatId.Vitality]);
        Assert.Equal(0f, scripted.StatSheet.Base[StatId.Energy]);
        Assert.Equal(1f, scripted.StatSheet.Base[StatId.AttackSpeed]);
    }

    [Fact]
    public void AWolfOnly_RuntimeSnapshot_CarriesNoEnemyPresentation()
    {
        using WorldSessionRuntime runtime = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors);
        ActorSnapshot snapshot = runtime.CaptureActorSnapshots().Single(actor => actor.Kind == CreatureKind.Enemy);

        Assert.Equal(wolf.Combatant.Actor.Id, snapshot.ActorId);
        Assert.Equal(0, snapshot.EnemyLevel);
        Assert.Null(snapshot.VisualKey);
        Assert.Equal(ActorFieldGroups.All, ActorWireRecord.Full(snapshot).Groups);
    }

    [Fact]
    public void ATemplateMaxHealth_ReplacesTheFormulaOnSpawn_AndARestoredRowKeepsItsOwn()
    {
        EnemyTemplate tough = Dire() with { MaxHealth = 700 };
        EnemyActor spawned = EnemyFactory.Spawn(tough, Vector3.Zero, 2100, Events, CombatFormulaService.Default);
        Assert.Equal(700, spawned.Combatant.Health.MaxHealth);
        Assert.Equal(700, spawned.Combatant.Health.CurrentHealth);
        Assert.Equal(200, EnemyFactory.Spawn(Dire(), Vector3.Zero, 2101, Events, CombatFormulaService.Default).Combatant.Health.MaxHealth);

        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: 16, vitality: 30, attackSpeed: 1.0f, currentHealth: 90, maxHealth: 700, "DireWolf", level: 3);
        EnemyActor restored = EnemyFactory.Restore(row, tough, 2102, Events, CombatFormulaService.Default);
        Assert.Equal(700, restored.Combatant.Health.MaxHealth);
        Assert.Equal(90, restored.Combatant.Health.CurrentHealth);

        PersistedEnemyInstance older = WolfIntervalBitsTest.Row(strength: 16, vitality: 30, attackSpeed: 1.0f, currentHealth: 150, maxHealth: 300, "DireWolf", level: 3);
        Assert.Equal(300, EnemyFactory.Restore(older, tough, 2103, Events, CombatFormulaService.Default).Combatant.Health.MaxHealth);
    }
}
