using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.BugHunt;

// Bug hunt, transfer packet, BH-XFER-5: the melee arc collected at most 16 candidates and only then
// threw out the ones behind a wall. A crowd whose nearest 16 stood behind a cube hid every reachable
// enemy, so the cast hit nothing while its mana and its cooldown had already been spent. The cap is
// there precisely so blocked enemies do not use up the skill's targets; it just was not big enough.
public sealed class XferMeleeArcCapTests
{
    private static readonly PlayerSessionId SessionA = new("session-arc");

    // The caster's eye, looking down -Z, with cell (5, 1, 5) free for the wall.
    private static readonly Vector3 Eye = new(5.5f, 1.5f, 6.5f);

    // Straight through the wall cell, well inside the 3.5 m arc.
    private static readonly Vector3 BehindTheWall = new(5.5f, 1.5f, 4.6f);

    // Off to the side, a clear line from the eye, further away than the whole crowd.
    private static readonly Vector3 AroundTheWall = new(7.5f, 1.5f, 5.0f);

    private static SkillDefinition Cleave()
    {
        Assert.True(ContentCatalog.Shipped.TryGetSkill("skill.cleave", out SkillDefinition? skill));
        return skill!;
    }

    // BH-XFER-5: twenty enemies behind the cube, one reachable beside it. The reachable one must be hit.
    [Fact]
    public void ACrowdBehindAWall_DoesNotHideTheReachableEnemy()
    {
        Fixture fixture = new();
        Assert.NotNull(fixture.Map.TryAddCube(new Vector3(5.5f, 1.5f, 5.5f), isStatic: true, withPhysics: false));
        for (int i = 0; i < 20; i++)
        {
            fixture.AddEnemy(BehindTheWall + new Vector3((i % 5) * 0.01f, 0f, (i / 5) * 0.01f));
        }

        EnemyActor reachable = fixture.AddEnemy(AroundTheWall);

        AbilityUsedResult used = fixture.Cast(tick: 10UL);

        Assert.True(used.TargetsHit >= 1, "the reachable enemy was hidden by the crowd behind the wall");
        Assert.True(
            reachable.Combatant.Health.CurrentHealth < reachable.Combatant.Health.MaxHealth,
            "the reachable enemy took no damage");
    }

    // Control: the wall still blocks. With nothing reachable the cast hits nothing, and the crowd behind
    // the cube is untouched -- raising the cap must not turn the arc into an x-ray.
    [Fact]
    public void ACrowdEntirelyBehindAWall_IsStillNotHit()
    {
        Fixture fixture = new();
        Assert.NotNull(fixture.Map.TryAddCube(new Vector3(5.5f, 1.5f, 5.5f), isStatic: true, withPhysics: false));
        List<EnemyActor> crowd = [];
        for (int i = 0; i < 20; i++)
        {
            crowd.Add(fixture.AddEnemy(BehindTheWall + new Vector3((i % 5) * 0.01f, 0f, (i / 5) * 0.01f)));
        }

        AbilityUsedResult used = fixture.Cast(tick: 10UL);

        Assert.Equal(0, used.TargetsHit);
        foreach (EnemyActor enemy in crowd)
        {
            Assert.Equal(enemy.Combatant.Health.MaxHealth, enemy.Combatant.Health.CurrentHealth);
        }
    }

    // Control: the skill's own target count still caps what a clear arc hits. The collect cap is headroom
    // for blocked candidates, not a licence to hit more than maxTargets.
    [Fact]
    public void AClearCrowd_StillHitsOnlyTheSkillsTargetCount()
    {
        Fixture fixture = new();
        SkillDefinition cleave = Cleave();
        for (int i = 0; i < 20; i++)
        {
            fixture.AddEnemy(new Vector3(4.6f + (i * 0.09f), 1.5f, 5.0f));
        }

        AbilityUsedResult used = fixture.Cast(tick: 10UL);

        Assert.Equal(cleave.MaxTargets, used.TargetsHit);
    }

    private sealed class Fixture
    {
        private int _nextActorId = 4400;

        public Fixture()
        {
            Resolver = new AuthoritativeCombatResolver(
                7UL,
                () => Map,
                Enemies,
                WorldObjects,
                Events,
                CombatFormulaService.Default,
                Host);
        }

        public Map Map { get; } = new(physics: null, generateStartupTerrain: false, width: 12, height: 8, depth: 12, headless: true);

        public EnemyRegistry Enemies { get; } = new();

        public WorldObjectRegistry WorldObjects { get; } = new();

        public SceneEventHub Events { get; } = new();

        public SilentArcHost Host { get; } = new();

        public AuthoritativeCombatResolver Resolver { get; }

        public EnemyActor AddEnemy(Vector3 at)
        {
            Vector3 position = at;
            EnemyCombatant combatant = new(
                () => position,
                Events,
                actorId: _nextActorId++,
                maxHealth: 1_000_000,
                attackDamage: 1,
                attackIntervalSeconds: 1f);
            EnemyActor enemy = new(
                combatant,
                position,
                aggroRange: 0f,
                attackRange: 0f,
                moveSpeed: 0f,
                displayName: "Wolf",
                level: 1);
            Enemies.Add(enemy);
            return enemy;
        }

        public AbilityUsedResult Cast(ulong tick)
        {
            PlayerCombatant attacker = new(() => Eye);
            Host.Combatants[SessionA] = attacker;
            SkillUseIntent intent = new(Cleave().Key);
            AuthoritativeActionResult result = Resolver.ProcessSkillUse(
                new PlayerActionMessage(SessionA, 1, intent),
                Cleave(),
                Eye,
                -Vector3.UnitZ,
                attacker,
                tick);
            Assert.True(result.Accepted);
            return Assert.IsType<AbilityUsedResult>(result.Payload);
        }
    }

    private sealed class SilentArcHost : IAuthoritativeCombatHost
    {
        public Dictionary<PlayerSessionId, PlayerCombatant> Combatants { get; } = [];

        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            bool found = Combatants.TryGetValue(sessionId, out PlayerCombatant? value);
            combatant = value;
            return found;
        }

        public void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant)
        {
        }

        public void DeletePersistedEnemy(int enemyId)
        {
        }

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick, Vector3 deathPosition)
        {
        }

        public WorldObjectRecord RepairCubeRecord(WorldObjectRecord record) => record;

        public void UpdatePersistedCubeHealth(WorldObjectRecord record)
        {
        }

        public void DeletePersistedPlacedCube(int cubeId)
        {
        }

        public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell)
        {
        }

        public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell)
        {
        }

        public bool CanDamageCube(PlayerSessionId attacker, WorldObjectRecord record) => true;
    }
}
