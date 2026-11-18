using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-AI-1: the primary melee attack resolved an enemy from a pure ray/radius test with no map
// lookup, so a player behind a single cube killed everything inside the 4 m reach while the wolf's
// own bite line was blocked by that same cube. The hit now goes through the file's IsBlockedByWall
// check, the one bolts, the melee arc and PvP already use.
public sealed class AiMeleeWallTests
{
    private const int EnemyActorId = 4300;
    private static readonly PlayerSessionId SessionA = new("session-a");

    // The enemy stands at z = 3.5, the attacker's eye at z = 6.5 looking down -Z: 3 m apart, inside
    // the 4 m primary reach, with cell (5, 1, 5) free for the wall.
    private static readonly Vector3 EnemyPosition = new(5.5f, 1.5f, 3.5f);
    private static readonly Vector3 Eye = new(5.5f, 1.5f, 6.5f);

    [Fact]
    public void APrimaryMeleeSwing_WithAClearLine_StillHitsTheEnemy()
    {
        Fixture fixture = new();
        EnemyActor enemy = fixture.AddEnemy();

        CombatResolutionResult payload = fixture.Swing(tick: 10UL);

        Assert.Equal(EnemyActorId, payload.TargetActorId);
        Assert.True(payload.DamageApplied > 0, "a clear swing must still land");
        Assert.True(
            enemy.Combatant.Health.CurrentHealth < enemy.Combatant.Health.MaxHealth,
            "the enemy must have taken the damage");
    }

    [Fact]
    public void APrimaryMeleeSwing_ThroughACube_HitsNothing()
    {
        Fixture fixture = new();
        EnemyActor enemy = fixture.AddEnemy();
        Assert.NotNull(fixture.Map.TryAddCube(new Vector3(5.5f, 1.5f, 5.5f), isStatic: true, withPhysics: false));

        CombatResolutionResult payload = fixture.Swing(tick: 10UL);

        // The swing falls through to the cube in the way, exactly as a blocked bolt does; what it must
        // never do is reach the enemy behind it.
        Assert.NotEqual(EnemyActorId, payload.TargetActorId);
        Assert.Equal(enemy.Combatant.Health.MaxHealth, enemy.Combatant.Health.CurrentHealth);
        Assert.True(enemy.IsActive);
    }

    // The mirror rule: the wolf cannot bite through the same cube, which is the asymmetry that made
    // the wall-less melee hit a safe spot.
    [Fact]
    public void TheSameCube_AlsoBlocksTheWolfsOwnBiteLine()
    {
        Fixture fixture = new();
        Assert.NotNull(fixture.Map.TryAddCube(new Vector3(5.5f, 1.5f, 5.5f), isStatic: true, withPhysics: false));

        Assert.False(EnemyMovementCollision.HasClearBiteLine(
            fixture.Map,
            EnemyPosition,
            WolfDefinition.CollisionHeight * 0.5f,
            Eye,
            EnemyActor.PlayerHalfHeight));
    }

    private sealed class Fixture
    {
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

        public SilentHost Host { get; } = new();

        public AuthoritativeCombatResolver Resolver { get; }

        public EnemyActor AddEnemy()
        {
            Vector3 position = EnemyPosition;
            EnemyCombatant combatant = new(
                () => position,
                Events,
                actorId: EnemyActorId,
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

        public CombatResolutionResult Swing(ulong tick)
        {
            PlayerCombatant attacker = new(() => Eye);
            Host.Combatants[SessionA] = attacker;
            CombatUseIntent intent = new(Eye, -Vector3.UnitZ);
            AuthoritativeActionResult result = Resolver.ProcessCombatUse(
                new PlayerActionMessage(SessionA, 1, intent),
                intent,
                PlayerInteractionMode.Combat,
                attacker,
                tick);
            Assert.True(result.Accepted);
            return Assert.IsType<CombatResolutionResult>(result.Payload);
        }
    }

    private sealed class SilentHost : IAuthoritativeCombatHost
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
