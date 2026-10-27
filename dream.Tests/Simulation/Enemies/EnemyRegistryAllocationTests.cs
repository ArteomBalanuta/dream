using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Enemies;

// MEM-3: EnemyRegistry runs on the server tick (UpdateAll, HasActiveActors, actor lookups) and
// on every client snapshot (TryGetEnemyByActorId and RemoveAllExcept from RemoteEnemyStateApplier).
// Before MEM-3 (bytes per call, Debug, measured by these tests on the original code): the client
// snapshot lookups for three wolves 352, ApplyActorSnapshots 352, UpdateAll with a chasing wolf
// on a map 152, HasActiveActors with two TryGetActorById calls 176. After: 0 for each.
public class EnemyRegistryAllocationTests
{
    private readonly ITestOutputHelper _output;

    public EnemyRegistryAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ClientSnapshotLookups_ThreeWolves_AllocateNothing()
    {
        EnemyRegistry registry = new();
        ActorSnapshot[] actors =
        [
            Enemy(3000, new Vector3(4f, 0f, 6f)),
            new ActorSnapshot(7, CreatureKind.Player, "session-a", Vector3.One, -Vector3.UnitZ, 100, 100, true),
            Enemy(3001, new Vector3(5f, 0f, 6f)),
            Enemy(3002, new Vector3(6f, 0f, 6f))
        ];
        RemoteEnemyStateApplier.ApplyActorSnapshots(registry, actors);
        HashSet<int> active = [3000, 3001, 3002];

        long lookupBytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            registry.TryGetEnemyByActorId(3000, out _);
            registry.TryGetEnemyByActorId(3001, out _);
            registry.TryGetEnemyByActorId(3002, out _);
            registry.RemoveAllExcept(active);
        });
        long applyBytes = AllocationAssert.MeasureBytesPerIteration(
            () => RemoteEnemyStateApplier.ApplyActorSnapshots(registry, actors));

        _output.WriteLine($"Registry lookups and RemoveAllExcept: {lookupBytes} B; ApplyActorSnapshots: {applyBytes} B");
        Assert.Equal(3, registry.ActiveActors.Count);
        Assert.True(lookupBytes <= 0, $"Registry lookups allocated {lookupBytes} B.");
        Assert.True(applyBytes <= 0, $"ApplyActorSnapshots allocated {applyBytes} B.");
    }

    [Fact]
    public void ServerLookups_AllocateNothing()
    {
        EnemyRegistry registry = new();
        registry.Add(CreateEnemy(new Vector3(1f, 1f, 1f), 2100));
        registry.Add(CreateEnemy(new Vector3(2f, 1f, 1f), 2101));
        bool found = false;

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            found = registry.HasActiveActors
                & registry.TryGetActorById(2101, out _)
                & !registry.TryGetActorById(9999, out _);
        });

        _output.WriteLine($"HasActiveActors and TryGetActorById: {bytes} B");
        Assert.True(found);
        Assert.True(bytes <= 0, $"Allocated {bytes} B.");
    }

    [Fact]
    public void UpdateAll_ChasingWolfOnMap_AllocatesNothing()
    {
        Map map = new(width: 16, height: 4, depth: 16);
        EnemyRegistry registry = new();
        EnemyActor wolf = CreateEnemy(new Vector3(2.5f, 1f, 2.5f), 2200, aggroRange: 10f);
        registry.Add(wolf);
        PlayerCombatant combatant = new(() => new Vector3(8.5f, 1.5f, 2.5f));
        IAuthoritativePlayerTarget[] targets =
        [
            new AuthoritativePlayerTarget(
                new PlayerSessionId("near"),
                actorId: 1,
                () => new Vector3(8.5f, 1.5f, 2.5f),
                () => -Vector3.UnitZ,
                combatant,
                PlayerInteractionMode.Building,
                _ => { })
        ];
        SceneEventHub events = new();

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            wolf.SetPosition(new Vector3(2.5f, 1f, 2.5f));
            registry.UpdateAll(SimulationClock.FixedDeltaSeconds, targets, events, map);
        });

        _output.WriteLine($"UpdateAll, one chasing wolf on a map: {bytes} B");
        Assert.Equal(EnemyBehaviorState.Chasing, wolf.BehaviorState);
        Assert.True(wolf.Position.X > 2.5f);
        Assert.True(bytes <= 0, $"Allocated {bytes} B.");
    }

    [Fact]
    public void Update_WithMap_MovesExactlyLikeTheResolverDelegate()
    {
        Map map = new(width: 5, height: 4, depth: 4);
        map.AddCube(new Vector3(2f, 1f, 0f), withPhysics: false);
        EnemyActor withMap = CreateEnemy(new Vector3(0.5f, 1f, 0.5f), 2300, aggroRange: 6f);
        EnemyActor withDelegate = CreateEnemy(new Vector3(0.5f, 1f, 0.5f), 2301, aggroRange: 6f);
        Vector3 player = new(4.5f, 1f, 0.6f);

        for (int step = 0; step < 30; step++)
        {
            withMap.Update(0.1f, player, map);
            withDelegate.Update(
                0.1f,
                player,
                (from, desiredDelta) => EnemyMovementCollision.ResolveStep(
                    map,
                    from,
                    desiredDelta,
                    withDelegate.CollisionRadius,
                    withDelegate.CollisionHeight));

            Assert.Equal(withDelegate.Position, withMap.Position);
            Assert.Equal(withDelegate.Forward, withMap.Forward);
            Assert.Equal(withDelegate.BehaviorState, withMap.BehaviorState);
        }

        // The cube in cell (2, 1, 0) stopped the chase short of it.
        Assert.True(withMap.Position.X < 2f);
    }

    [Fact]
    public void RemoveAllExcept_KeepsListedActorsInOrder()
    {
        EnemyRegistry registry = new();
        EnemyActor first = CreateEnemy(Vector3.Zero, 1);
        EnemyActor second = CreateEnemy(Vector3.Zero, 2);
        EnemyActor third = CreateEnemy(Vector3.Zero, 3);
        EnemyActor fourth = CreateEnemy(Vector3.Zero, 4);
        registry.Add(first);
        registry.Add(second);
        registry.Add(third);
        registry.Add(fourth);

        registry.RemoveAllExcept(new HashSet<int> { 4, 2 });

        Assert.Equal([second, fourth], registry.ActiveActors);
        Assert.True(registry.TryGetEnemyByActorId(4, out EnemyActor? found));
        Assert.Same(fourth, found);
        Assert.False(registry.TryGetEnemyByActorId(1, out EnemyActor? missing));
        Assert.Null(missing);
    }

    private static ActorSnapshot Enemy(int actorId, Vector3 position)
    {
        return new ActorSnapshot(
            actorId,
            CreatureKind.Enemy,
            null,
            position,
            -Vector3.UnitZ,
            80,
            100,
            true,
            DisplayName: "Wolf",
            EnemyBehaviorState: EnemyBehaviorState.Chasing);
    }

    private static EnemyActor CreateEnemy(Vector3 position, int actorId, float aggroRange = 5f)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, position, aggroRange: aggroRange, attackRange: 1.25f, moveSpeed: 3f);
    }
}
