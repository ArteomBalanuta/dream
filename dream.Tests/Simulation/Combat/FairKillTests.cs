using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Combat;

// M2-C: a kill's experience is shared among the players who hurt the enemy and its loot goes to the
// top damage dealer, not to whoever lands the last hit.
public class FairKillTests
{
    private static readonly PlayerSessionId A = new("session-a");
    private static readonly PlayerSessionId B = new("session-b");
    private static readonly PlayerSessionId C = new("session-c");
    private const int Hp = 1000;

    [Fact]
    public void TwoPlayersOneWolf_XpIsSplit_AndLootGoesToTopDealerNotLastHit()
    {
        Harness h = new(enemyLevel: 9); // reward 9 * 14 = 126
        PlayerCombatant a = h.Join(A);
        PlayerCombatant b = h.Join(B);
        h.Preload(A, 975); // 25 left; the weakest hit is 25

        Assert.True(h.Hit(B).TargetDied);

        Assert.Equal(63, a.Experience);
        Assert.Equal(63, b.Experience);
        Assert.Equal([$"GrantLoot session-a Wolf {Harness.EnemyId}"], h.Host.Loots);
    }

    [Fact]
    public void SharedXp_RemainderGoesToLowestSessionIdsFirst_AndLootTieGoesToLowestId()
    {
        Harness h = new(enemyLevel: 1); // reward 14 over 3 = 5, 5, 4
        PlayerCombatant a = h.Join(A);
        PlayerCombatant b = h.Join(B);
        PlayerCombatant c = h.Join(C);
        // Hit order differs from id order; A and C tie on top damage, B finishes well below them.
        h.Preload(C, 360);
        h.Preload(B, 255);
        h.Preload(A, 360);

        Assert.True(h.Hit(B).TargetDied);

        Assert.Equal(5, a.Experience);
        Assert.Equal(5, b.Experience);
        Assert.Equal(4, c.Experience);
        Assert.Equal([$"GrantLoot session-a Wolf {Harness.EnemyId}"], h.Host.Loots);
    }

    [Fact]
    public void ContributorWhoLeftMidFight_GetsNothing_AndNothingThrows()
    {
        Harness h = new(enemyLevel: 9);
        PlayerCombatant a = h.Join(A);
        PlayerCombatant b = h.Join(B);
        h.Preload(A, 975);
        h.Host.Combatants.Remove(A); // the top dealer left

        Assert.True(h.Hit(B).TargetDied);

        Assert.Equal(0, a.Experience);
        Assert.Equal(126, b.Experience);
        Assert.Equal([$"GrantLoot session-b Wolf {Harness.EnemyId}"], h.Host.Loots);
    }

    [Fact]
    public void SoloKill_GivesTheFullRewardAndTheLoot()
    {
        Harness h = new(enemyLevel: 1);
        PlayerCombatant a = h.Join(A);
        h.Enemy.Combatant.ApplyDamage(975); // not recorded against anyone

        Assert.True(h.Hit(A).TargetDied);

        Assert.Equal(14, a.Experience);
        Assert.Equal([$"GrantLoot session-a Wolf {Harness.EnemyId}"], h.Host.Loots);
    }

    [Fact]
    public void Contributors_AreCappedAtEight_AccumulatePerSession_AndRecordWithoutAllocating()
    {
        EnemyActor enemy = Harness.CreateEnemy(new SceneEventHub(), 1);
        for (int i = 0; i < 10; i++)
        {
            enemy.RecordDamage(new PlayerSessionId($"s{i}"), 10 + i);
        }

        Assert.Equal(EnemyActor.MaxContributors, enemy.Contributors.Length);
        enemy.RecordDamage(new PlayerSessionId("s7"), 5);
        Assert.Equal(22, Find(enemy, "s7"));

        // The newcomers s8 (18) and s9 (19) displaced s0 (10) and s1 (11).
        Assert.Equal(-1, Find(enemy, "s0"));
        Assert.Equal(-1, Find(enemy, "s1"));
        Assert.Equal(18, Find(enemy, "s8"));
        Assert.Equal(19, Find(enemy, "s9"));

        PlayerSessionId again = new("s7");
        enemy.RecordDamage(again, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            enemy.RecordDamage(again, 1);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static int Find(EnemyActor enemy, string session)
    {
        foreach (EnemyDamageContribution entry in enemy.Contributors)
        {
            if (entry.SessionId.Value == session)
            {
                return entry.Damage;
            }
        }

        return -1;
    }

    private sealed class Harness
    {
        public const int EnemyId = 4100;
        private readonly AuthoritativeCombatResolver _resolver;
        private readonly Map _map = new(physics: null, generateStartupTerrain: false, width: 10, height: 8, depth: 10, headless: true);
        private readonly EnemyRegistry _enemies = new();
        private ulong _tick;
        private ulong _sequence;

        public Harness(int enemyLevel)
        {
            SceneEventHub events = new();
            Enemy = CreateEnemy(events, enemyLevel);
            _enemies.Add(Enemy);
            _resolver = new AuthoritativeCombatResolver(
                1UL, () => _map, _enemies, new WorldObjectRegistry(), events, CombatFormulaService.Default, Host);
        }

        public static EnemyActor CreateEnemy(SceneEventHub events, int level)
        {
            Vector3 position = new(5f, 1f, 3f);
            EnemyCombatant combatant = new(() => position, events, actorId: EnemyId, maxHealth: Hp, attackDamage: 1, attackIntervalSeconds: 1f);
            return new EnemyActor(combatant, position, 0f, 0f, 0f, displayName: "Wolf", level: level);
        }

        public EnemyActor Enemy { get; }

        public Host Host { get; } = new();

        public PlayerCombatant Join(PlayerSessionId id)
        {
            PlayerCombatant combatant = new(() => Vector3.Zero);
            Host.Combatants[id] = combatant;
            return combatant;
        }

        // Damage dealt earlier in the fight, recorded as the resolver would.
        public void Preload(PlayerSessionId id, int amount)
        {
            DamageReceipt receipt = Enemy.Combatant.ApplyDamage(amount);
            Enemy.RecordDamage(id, receipt.AppliedAmount);
        }

        public CombatResolutionResult Hit(PlayerSessionId id)
        {
            _tick += 100;
            AuthoritativeActionResult result = _resolver.ProcessCombatUse(
                new PlayerActionMessage(id, ++_sequence, new CombatUseIntent(Vector3.Zero, Vector3.UnitX)),
                new CombatUseIntent(Enemy.Combatant.GetCombatAnchorWorldPosition() + new Vector3(0f, 0f, 2f), -Vector3.UnitZ),
                PlayerInteractionMode.Combat,
                Host.Combatants[id],
                _tick);
            return Assert.IsType<CombatResolutionResult>(result.Payload);
        }
    }

    private sealed class Host : IAuthoritativeCombatHost
    {
        public Dictionary<PlayerSessionId, PlayerCombatant> Combatants { get; } = [];

        public List<string> Loots { get; } = [];

        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            bool found = Combatants.TryGetValue(sessionId, out PlayerCombatant? value);
            combatant = value;
            return found;
        }

        public void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant) { }

        public void DeletePersistedEnemy(int enemyId) { }

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick)
            => Loots.Add($"GrantLoot {killer.Value} {enemyKind} {enemyActorId}");

        public WorldObjectRecord RepairCubeRecord(WorldObjectRecord record) => record;

        public void UpdatePersistedCubeHealth(WorldObjectRecord record) { }

        public void DeletePersistedPlacedCube(int cubeId) { }

        public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell) { }

        public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell) { }
    }
}
