using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Combat;

// M4 workstream B at the resolver: the skill shapes, with no runtime. The shipped skill definitions are
// used as authored, so a content change to a range or a percent shows up here.
public class SkillResolutionTests
{
    private static readonly PlayerSessionId Caster = new("caster");

    private static SkillDefinition Skill(string key) =>
        ContentCatalog.Shipped.TryGetSkill(key, out SkillDefinition? skill) ? skill! : throw new InvalidOperationException(key);

    // Stands at (5.5, y, 5.5) looking down -Z. Enemies are placed relative to that eye.
    private sealed class Rig
    {
        public Rig(bool pvp = false)
        {
            Host.PvpEnabled = pvp;
            Resolver = new AuthoritativeCombatResolver(
                77UL, () => Map, Enemies, WorldObjects, Events, CombatFormulaService.Default, Host);
            Attacker = new PlayerCombatant(() => Vector3.Zero, Events, actorId: 1);
            Host.Combatants[Caster] = Attacker;
            Events.Subscribe<DamageAppliedEvent>(damage => Damage.Add(damage));
        }

        public Map Map { get; } = new(physics: null, generateStartupTerrain: false, width: 12, height: 8, depth: 12, headless: true);

        public EnemyRegistry Enemies { get; } = new();

        public WorldObjectRegistry WorldObjects { get; } = new();

        public SceneEventHub Events { get; } = new();

        public Host Host { get; } = new();

        public AuthoritativeCombatResolver Resolver { get; }

        public PlayerCombatant Attacker { get; }

        public List<DamageAppliedEvent> Damage { get; } = [];

        public Vector3 Eye => new(5.5f, 1.5f, 8.5f);

        // An enemy whose anchor is offset from the eye (x right, z behind the caster when positive).
        public EnemyActor AddEnemy(int id, Vector3 anchorOffset, int health = 1_000_000)
        {
            Vector3 anchor = Eye + anchorOffset;
            Vector3 position = anchor - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f);
            EnemyCombatant combatant = new(() => position, Events, actorId: id, maxHealth: health, attackDamage: 1, attackIntervalSeconds: 1f);
            EnemyActor enemy = new(combatant, position, aggroRange: 0f, attackRange: 0f, moveSpeed: 0f, displayName: "Wolf", level: 1);
            Enemies.Add(enemy);
            return enemy;
        }

        public AbilityUsedResult Cast(string key, Vector3? aim = null, ulong tick = 10)
        {
            PlayerActionMessage message = new(Caster, 1, new SkillUseIntent(key, aim));
            AuthoritativeActionResult result = Resolver.ProcessSkillUse(message, Skill(key), Eye, aim ?? -Vector3.UnitZ, Attacker, tick);
            Assert.True(result.Accepted);
            return Assert.IsType<AbilityUsedResult>(result.Payload);
        }
    }

    private sealed class Host : IAuthoritativeCombatHost
    {
        public Dictionary<PlayerSessionId, PlayerCombatant> Combatants { get; } = [];

        public List<string> Calls { get; } = [];

        public bool PvpEnabled { get; set; }

        public List<CombatTargetCandidate> Players { get; } = [];

        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            bool found = Combatants.TryGetValue(sessionId, out PlayerCombatant? value);
            combatant = value;
            return found;
        }

        public void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant) { }

        public void DeletePersistedEnemy(int enemyId) => Calls.Add($"DeletePersistedEnemy {enemyId}");

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick, Vector3 deathPosition)
            => Calls.Add($"GrantLoot {killer.Value} {enemyActorId}");

        public WorldObjectRecord RepairCubeRecord(WorldObjectRecord record) => record;

        public void UpdatePersistedCubeHealth(WorldObjectRecord record) { }

        public void DeletePersistedPlacedCube(int cubeId) { }

        public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell) { }

        public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell) { }

        public CombatTargetPolicy TargetPolicy => new(PvpEnabled);

        public void FillPvpTargets(PlayerSessionId attacker, List<CombatTargetCandidate> into) => into.AddRange(Players);

        public PlayerCombatant? ResolvePvpVictim(int actorId) => Victims.GetValueOrDefault(actorId);

        public Dictionary<int, PlayerCombatant> Victims { get; } = [];
    }

    [Fact]
    public void Cleave_HitsAtMostMaxTargets_NearestFirst()
    {
        Rig rig = new();
        SkillDefinition cleave = Skill("skill.cleave");
        Assert.Equal(4, cleave.MaxTargets);
        for (int i = 0; i < 6; i++)
        {
            rig.AddEnemy(100 + i, new Vector3(0f, 0f, -(0.8f + (i * 0.4f))));
        }

        AbilityUsedResult result = rig.Cast(cleave.Key);

        Assert.Equal(4, result.TargetsHit);
        Assert.Equal(4, rig.Damage.Count);
        Assert.Equal([100, 101, 102, 103], rig.Damage.Select(d => d.Actor.Id).ToArray());
        Assert.Equal(rig.Damage.Sum(d => d.Amount), result.DamageApplied);
    }

    [Fact]
    public void Cleave_HitsInsideTheArcOnly_AndOutOfRangeNotAtAll()
    {
        Rig rig = new();
        SkillDefinition cleave = Skill("skill.cleave");
        EnemyActor ahead = rig.AddEnemy(100, new Vector3(0f, 0f, -2f));
        EnemyActor edgeIn = rig.AddEnemy(101, new Vector3(2.4f, 0f, -2f * 0.9f));   // ~53 degrees off the line: inside 60
        EnemyActor edgeOut = rig.AddEnemy(102, new Vector3(2.4f, 0f, -0.5f));        // ~78 degrees: outside
        EnemyActor behind = rig.AddEnemy(103, new Vector3(0f, 0f, 2f));
        EnemyActor far = rig.AddEnemy(104, new Vector3(0f, 0f, -(cleave.Range + 1f)));

        AbilityUsedResult result = rig.Cast(cleave.Key);

        HashSet<int> hit = rig.Damage.Select(d => d.Actor.Id).ToHashSet();
        Assert.Equal(2, result.TargetsHit);
        Assert.Contains(100, hit);
        Assert.Contains(101, hit);
        Assert.DoesNotContain(102, hit);
        Assert.DoesNotContain(103, hit);
        Assert.DoesNotContain(104, hit);
        Assert.Equal(ahead.Combatant.Health.MaxHealth - rig.Damage.First(d => d.Actor.Id == 100).Amount, ahead.Combatant.Health.CurrentHealth);
        Assert.Equal(edgeOut.Combatant.Health.MaxHealth, edgeOut.Combatant.Health.CurrentHealth);
        Assert.Equal(behind.Combatant.Health.MaxHealth, behind.Combatant.Health.CurrentHealth);
        _ = edgeIn;
        _ = far;
    }

    [Fact]
    public void Cleave_SurvivesAKillThatPrunesTheRegistryMidLoop_AndPaysEveryKill()
    {
        Rig rig = new();
        rig.Attacker.ApplyProgression(1, 0);
        EnemyActor first = rig.AddEnemy(100, new Vector3(0f, 0f, -1f), health: 1);
        EnemyActor second = rig.AddEnemy(101, new Vector3(0.3f, 0f, -1.5f), health: 1);
        EnemyActor survivor = rig.AddEnemy(102, new Vector3(-0.3f, 0f, -2f));

        AbilityUsedResult result = rig.Cast("skill.cleave");

        Assert.Equal(3, result.TargetsHit);
        Assert.False(first.Combatant.Health.IsAlive);
        Assert.False(second.Combatant.Health.IsAlive);
        Assert.True(survivor.Combatant.Health.IsAlive);
        Assert.Equal(["DeletePersistedEnemy 100", "GrantLoot caster 100", "DeletePersistedEnemy 101", "GrantLoot caster 101"], rig.Host.Calls);
        Assert.Equal([survivor], rig.Enemies.ActiveActors.ToArray());
        // A skill kill pays the contributor like a primary kill: experience reaches the caster.
        Assert.True(rig.Attacker.Experience > 0);
    }

    [Fact]
    public void Bolt_HitsTheFirstEnemyOnTheRay_ScaledByItsPercent()
    {
        Rig rig = new();
        SkillDefinition bolt = Skill("skill.bolt");
        EnemyActor near = rig.AddEnemy(100, new Vector3(0f, 0f, -6f));
        EnemyActor far = rig.AddEnemy(101, new Vector3(0f, 0f, -12f));

        AbilityUsedResult result = rig.Cast(bolt.Key, tick: 33);

        Assert.Equal(1, result.TargetsHit);
        PlayerAttackResolution roll = AuthoritativeCombatResolver.ResolveSeededAttack(
            77UL, rig.Attacker, new PlayerActionMessage(Caster, 1, new SkillUseIntent(bolt.Key)), 33, Seed.FromInt(100));
        Assert.Equal(Math.Max(1, roll.Damage * bolt.DamagePercent / 100), result.DamageApplied);
        Assert.True(near.Combatant.Health.CurrentHealth < near.Combatant.Health.MaxHealth);
        Assert.Equal(far.Combatant.Health.MaxHealth, far.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void Bolt_StopsAtAWall_AndNeverDamagesTheCube()
    {
        Rig rig = new();
        EnemyActor target = rig.AddEnemy(100, new Vector3(0f, 0f, -8f));
        // Hits with no wall between.
        Assert.Equal(1, rig.Cast("skill.bolt").TargetsHit);
        int healthBefore = target.Combatant.Health.CurrentHealth;

        Vector3 anchor = target.Combatant.GetCombatAnchorWorldPosition();
        Cube? wall = rig.Map.TryAddCube(new Vector3(5.5f, anchor.Y, 5.5f), isStatic: true, withPhysics: false);
        Assert.NotNull(wall);
        rig.WorldObjects.RegisterCube(wall!, rig.Map, actorId: null, ownerPlayerName: "someone");

        AbilityUsedResult blocked = rig.Cast("skill.bolt", tick: 11);

        Assert.Equal(0, blocked.TargetsHit);
        Assert.Equal(0, blocked.DamageApplied);
        Assert.Equal(healthBefore, target.Combatant.Health.CurrentHealth);
        Assert.True(rig.WorldObjects.TryResolveCubeRecord(rig.Map, wall!) is { } record
            && record.Health.CurrentHealth == record.Health.MaxHealth);
    }

    [Fact]
    public void Cleave_DoesNotReachAnEnemyBehindAWall()
    {
        Rig rig = new();
        EnemyActor target = rig.AddEnemy(100, new Vector3(0f, 0f, -3f));
        Vector3 anchor = target.Combatant.GetCombatAnchorWorldPosition();
        Assert.NotNull(rig.Map.TryAddCube(new Vector3(5.5f, anchor.Y, 6.5f), isStatic: true, withPhysics: false));

        Assert.Equal(0, rig.Cast("skill.cleave").TargetsHit);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void Mend_RestoresTheHealAmount_AndCapsAtMax()
    {
        Rig rig = new();
        SkillDefinition mend = Skill("skill.mend");
        int max = rig.Attacker.Health.MaxHealth;
        rig.Attacker.Health.SetAuthoritativeState(max - 10, max);

        AbilityUsedResult first = rig.Cast(mend.Key);

        Assert.Equal(10, first.HealthRestored);
        Assert.Equal(max, rig.Attacker.Health.CurrentHealth);
        rig.Attacker.Health.SetAuthoritativeState(max - mend.HealAmount - 5, max);
        AbilityUsedResult full = rig.Cast(mend.Key);
        Assert.Equal(mend.HealAmount, full.HealthRestored);
        Assert.Equal(max - 5, rig.Attacker.Health.CurrentHealth);
        Assert.Equal(0, full.TargetsHit);
    }

    [Fact]
    public void Mend_ForADeadCaster_RestoresNothing()
    {
        Rig rig = new();
        rig.Attacker.Health.SetAuthoritativeState(0, rig.Attacker.Health.MaxHealth);

        Assert.Equal(0, rig.Cast("skill.mend").HealthRestored);
        Assert.False(rig.Attacker.Health.IsAlive);
    }

    [Fact]
    public void ACastThatHitsNothing_IsAccepted()
    {
        Rig rig = new();

        AbilityUsedResult result = rig.Cast("skill.bolt");

        Assert.Equal(new AbilityUsedResult("skill.bolt", 0, 0, 0), result);
        Assert.Empty(rig.Damage);
    }

    [Fact]
    public void DamagePercent100_IsExactlyThePrimaryAttackDamage()
    {
        // A primary attack and a 100 percent hit on the same roll inputs differ by nothing.
        Rig rig = new();
        EnemyActor enemy = rig.AddEnemy(100, new Vector3(0f, 0f, -2f));
        AuthoritativeActionResult primary = rig.Resolver.ProcessCombatUse(
            new PlayerActionMessage(Caster, 1, new CombatUseIntent(rig.Eye, -Vector3.UnitZ)),
            new CombatUseIntent(rig.Eye, -Vector3.UnitZ),
            PlayerInteractionMode.Combat,
            rig.Attacker,
            50);
        int applied = Assert.IsType<CombatResolutionResult>(primary.Payload).DamageApplied;
        PlayerAttackResolution roll = AuthoritativeCombatResolver.ResolveSeededAttack(
            77UL, rig.Attacker, new PlayerActionMessage(Caster, 1, new CombatUseIntent(rig.Eye, -Vector3.UnitZ)), 50, Seed.FromInt(100));
        Assert.Equal(roll.Damage, applied);
        Assert.True(enemy.Combatant.Health.CurrentHealth < enemy.Combatant.Health.MaxHealth);
    }

    [Fact]
    public void CollectInArc_IsNearestFirst_TiesByActorId_AndKeepsOnlyTheNearest()
    {
        Rig rig = new();
        rig.AddEnemy(205, new Vector3(0f, 0f, -2f));
        rig.AddEnemy(201, new Vector3(0f, 0f, -2f));
        rig.AddEnemy(203, new Vector3(0f, 0f, -1f));
        rig.AddEnemy(204, new Vector3(0f, 0f, -3f));
        List<EnemyActor> into = [];

        rig.Enemies.CollectInArc(rig.Eye, -Vector3.UnitZ, 4f, -1f, 3, into);

        Assert.Equal([203, 201, 205], into.Select(e => e.Combatant.Actor.Id).ToArray());
    }

    [Fact]
    public void Pvp_Off_NoPlayerIsHitBySkillsOrTheHost()
    {
        Rig rig = new(pvp: false);
        PlayerCombatant victim = new(() => Vector3.Zero, rig.Events, actorId: 9);
        rig.Host.Victims[9] = victim;
        rig.Host.Players.Add(new CombatTargetCandidate(victim.Actor, rig.Eye + new Vector3(0f, 0f, -2f), 20));

        Assert.Equal(0, rig.Cast("skill.cleave").TargetsHit);
        Assert.Equal(0, rig.Cast("skill.bolt").TargetsHit);
        Assert.Equal(victim.Health.MaxHealth, victim.Health.CurrentHealth);
    }

    [Fact]
    public void Pvp_On_BoltAndCleaveHitPlayers_EnemiesFirst_NoRewards()
    {
        Rig rig = new(pvp: true);
        PlayerCombatant victim = new(() => Vector3.Zero, rig.Events, actorId: 9);
        rig.Host.Victims[9] = victim;
        rig.Host.Players.Add(new CombatTargetCandidate(victim.Actor, rig.Eye + new Vector3(0f, 0f, -2f), 20));
        int experience = rig.Attacker.Experience;

        AbilityUsedResult bolt = rig.Cast("skill.bolt");
        Assert.Equal(1, bolt.TargetsHit);
        Assert.True(victim.Health.CurrentHealth < victim.Health.MaxHealth);

        int afterBolt = victim.Health.CurrentHealth;
        AbilityUsedResult cleave = rig.Cast("skill.cleave");
        Assert.Equal(1, cleave.TargetsHit);
        Assert.True(victim.Health.CurrentHealth < afterBolt);

        // An enemy on the ray takes the bolt before a player does.
        rig.AddEnemy(100, new Vector3(0f, 0f, -1f));
        int victimHealth = victim.Health.CurrentHealth;
        Assert.Equal(1, rig.Cast("skill.bolt").TargetsHit);
        Assert.Equal(victimHealth, victim.Health.CurrentHealth);
        Assert.Equal(experience, rig.Attacker.Experience);
        Assert.DoesNotContain(rig.Host.Calls, call => call.StartsWith("GrantLoot", StringComparison.Ordinal));
    }

    [Fact]
    public void Pvp_On_AFatalHitKillsThePlayer_WithADeathEvent()
    {
        Rig rig = new(pvp: true);
        PlayerCombatant victim = new(() => Vector3.Zero, rig.Events, actorId: 9);
        victim.Health.SetAuthoritativeState(1, victim.Health.MaxHealth);
        List<int> died = [];
        rig.Events.Subscribe<ActorDiedEvent>(evt => died.Add(evt.Actor.Id));
        rig.Host.Victims[9] = victim;
        rig.Host.Players.Add(new CombatTargetCandidate(victim.Actor, rig.Eye + new Vector3(0f, 0f, -2f), 20));

        Assert.Equal(1, rig.Cast("skill.bolt").TargetsHit);

        Assert.False(victim.Health.IsAlive);
        Assert.Equal([9], died);
    }
}
