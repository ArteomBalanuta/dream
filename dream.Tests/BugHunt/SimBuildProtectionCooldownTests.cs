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

// BH-SIM-3: a swing at a build-protected cube damages nothing, so it must not spend the swing
// either. It used to run TryStartAttack first and never give the cooldown back, so a player
// standing in front of someone else's wall could not attack at all.
public sealed class SimBuildProtectionCooldownTests
{
    private static readonly PlayerSessionId SessionA = new("session-a");

    [Fact]
    public void ASwingAtAProtectedCube_DoesNotConsumeTheAttackCooldown()
    {
        Fixture fixture = new();
        Cube cube = Assert.IsType<Cube>(fixture.Map.TryAddCube(new Vector3(5f, 1f, 3f), withPhysics: false));
        fixture.WorldObjects.RegisterCube(cube, fixture.Map, maxHealth: 50);
        PlayerCombatant combatant = new(() => Vector3.Zero);
        PlayerActionMessage message = new(SessionA, 1, new CombatUseIntent(Vector3.Zero, Vector3.UnitX));
        CombatUseIntent intent = new(new Vector3(5.5f, 1.5f, 6f), -Vector3.UnitZ);

        AuthoritativeActionResult first = fixture.Resolver.ProcessCombatUse(
            message, intent, PlayerInteractionMode.Combat, combatant, tick: 3UL);
        AuthoritativeActionResult second = fixture.Resolver.ProcessCombatUse(
            message, intent, PlayerInteractionMode.Combat, combatant, tick: 4UL);

        Assert.False(first.Accepted);
        Assert.Equal("That belongs to someone else.", first.Rejection?.Reason);
        // The swing was given back, so the next one is refused for ownership again - not for cooldown.
        Assert.False(second.Accepted);
        Assert.Equal("That belongs to someone else.", second.Rejection?.Reason);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Resolver = new AuthoritativeCombatResolver(
                1UL,
                () => Map,
                Enemies,
                WorldObjects,
                Events,
                CombatFormulaService.Default,
                Host);
        }

        public Map Map { get; } = new(physics: null, generateStartupTerrain: false, width: 10, height: 8, depth: 10, headless: true);

        public EnemyRegistry Enemies { get; } = new();

        public WorldObjectRegistry WorldObjects { get; } = new();

        public SceneEventHub Events { get; } = new();

        public ProtectingHost Host { get; } = new();

        public AuthoritativeCombatResolver Resolver { get; }
    }

    // Owns every cube: CanDamageCube always refuses.
    private sealed class ProtectingHost : IAuthoritativeCombatHost
    {
        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            combatant = null;
            return false;
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

        public bool CanDamageCube(PlayerSessionId attacker, WorldObjectRecord record) => false;
    }
}
