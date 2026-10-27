using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.World;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Presentation;


public class WorldSessionPresentationStateActiveEnemiesTests
{
    private static readonly PlayerCombatStats PlayerStats = new() { Strength = 10, AttackSpeed = 1f, Vitality = 10 };

    [Fact]
    public void Create_Returns_The_Registry_List_Without_Copying_When_All_Enemies_Are_Active()
    {
        EnemyRegistry registry = CreateRegistry(4);

        WorldSessionPresentationState state = CreateState(registry.ActiveActors);

        Assert.Same(registry.ActiveActors, state.ActiveEnemies);
        Assert.Equal(registry.ActiveActors, state.ActiveEnemies);
    }

    [Fact]
    public void Create_Filters_Inactive_Enemies_In_Order()
    {
        EnemyRegistry registry = CreateRegistry(5);
        registry.ActiveActors[1].Combatant.ApplyDamage(999);
        registry.ActiveActors[3].Combatant.ApplyDamage(999);

        WorldSessionPresentationState state = CreateState(registry.ActiveActors);

        Assert.NotSame(registry.ActiveActors, state.ActiveEnemies);
        Assert.Equal(
            [registry.ActiveActors[0], registry.ActiveActors[2], registry.ActiveActors[4]],
            state.ActiveEnemies);
    }

    [Fact]
    public void Create_Returns_Empty_When_No_Enemy_Is_Active()
    {
        EnemyRegistry registry = CreateRegistry(2);
        registry.ActiveActors[0].Combatant.ApplyDamage(999);
        registry.ActiveActors[1].Combatant.ApplyDamage(999);

        WorldSessionPresentationState state = CreateState(registry.ActiveActors);

        Assert.Empty(state.ActiveEnemies);
    }

    [Fact]
    public void Create_Filters_A_Lazy_Sequence()
    {
        EnemyRegistry registry = CreateRegistry(3);
        registry.ActiveActors[0].Combatant.ApplyDamage(999);

        WorldSessionPresentationState state = CreateState(registry.ActiveActors.Select(actor => actor));

        Assert.Equal([registry.ActiveActors[1], registry.ActiveActors[2]], state.ActiveEnemies);
    }

    [Fact]
    public void Create_Allocates_Only_The_State_When_All_Enemies_Are_Active()
    {
        // Before MEM-5, Create allocated 496 B per call with four active enemies: the state and
        // its HUD snapshot, plus the Where iterator and the ToArray copy.
        EnemyRegistry registry = CreateRegistry(4);
        IReadOnlyList<EnemyActor> enemies = registry.ActiveActors;

        long bytes = AllocationAssert.MeasureBytesPerIteration(() => CreateState(enemies));

        long stateOnlyBytes = AllocationAssert.MeasureBytesPerIteration(() => CreateState(Array.Empty<EnemyActor>()));
        Assert.Equal(stateOnlyBytes, bytes);
    }

    [Fact]
    public void CapturePresentationState_Allocates_No_Stopwatch_Or_Enemy_Copy()
    {
        // Before MEM-5 a capture allocated 856 B: a Stopwatch for the export timing and the
        // Where iterator over the enemy registry came on top of the 744 B measured afterwards.
        // GFX-4 added four pre-draw doubles to the FramePerformanceSnapshot carried inside each
        // presentation state, so the same single state allocation is now 32 B larger (776 B).
        // No additional per-frame allocation was introduced.
        using ClientWorldSession session = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);

        AllocationAssert.AtMost(
            776,
            () => session.CapturePresentationState(WorldVisibility.MaxRenderDistance));
    }

    private static EnemyRegistry CreateRegistry(int count)
    {
        EnemyRegistry registry = new();
        for (int i = 1; i <= count; i++)
        {
            Vector3 position = new(i, 0f, 0f);
            EnemyCombatant combatant = new(
                () => position,
                actorId: i,
                maxHealth: 80,
                attackDamage: 10,
                attackIntervalSeconds: 1f);
            registry.Add(new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f));
        }

        return registry;
    }

    private static WorldSessionPresentationState CreateState(IEnumerable<EnemyActor> enemies)
    {
        PlayerCombatDerivedStats derivedStats = PlayerStats.Derive();
        return WorldSessionPresentationState.Create(
            camera: new Camera(Vector3.Zero),
            visibleWorld: new GeometryRenderExport(
                Array.Empty<GeometryCellKey>(),
                Array.Empty<GeometryRenderCell>(),
                Array.Empty<GeometryRenderBlockDescriptor>(),
                Array.Empty<Cube>()),
            activeEnemies: enemies,
            mapCollisionBodies: Array.Empty<BulletSharp.RigidBody>(),
            playerCollisionBody: null,
            new HealthState(derivedStats.MaxHealth),
            PlayerInteractionMode.Combat,
            derivedStats,
            PlayerStats,
            level: 1,
            experience: 0,
            experienceToNextLevel: 100);
    }
}
