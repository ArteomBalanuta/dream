using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Simulation.Enemies;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Input;

// The per-frame hover path: it must answer exactly as a click does, and allocate nothing.
public class WorldCursorPickerHoverTests
{
    private sealed class FloorWorld : INavWorld
    {
        public int Width => 32;

        public int Height => 16;

        public int Depth => 32;

        public bool IsBlocked(int x, int y, int z) => y == 0 || (x == 10 && y == 1 && z == 10);

        public bool IsSupport(int x, int y, int z) => IsBlocked(x, y, z);
    }

    private static EnemyActor Wolf(int actorId, Vector3 position)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: 80);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f);
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return ["in front of the cube", 8.5f, true, true];
        yield return ["behind the cube", 12.5f, true, false];
        yield return ["dropped from the registry", 8.5f, false, false];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HoverAgreesWithTheClickPick(string what, float wolfX, bool registered, bool expected)
    {
        Assert.NotNull(what);
        FloorWorld world = new();
        EnemyRegistry registry = new();
        EnemyActor wolf = Wolf(2000, new Vector3(wolfX, 1.5f, 10.5f));
        registry.Add(wolf);
        if (!registered)
        {
            registry.Clear();
        }

        Vector3 origin = new(6.5f, 1.5f, 10.5f);
        ClickPick click = WorldCursorPicker.Pick(world, registry, origin, Vector3.UnitX);
        bool clickAttacks = click.Kind == ClickPickKind.Enemy && click.Target is { IsAlive: true };
        EnemyActor? hover = WorldCursorPicker.PickHoveredEnemy(world, registry, origin, Vector3.UnitX);

        Assert.Equal(expected, clickAttacks);
        Assert.Equal(expected, hover is not null);
    }

    [Fact]
    public void HoverMissesAnEnemyOffTheRay()
    {
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(8.5f, 1.5f, 20.5f)));

        Assert.Null(WorldCursorPicker.PickHoveredEnemy(new FloorWorld(), registry, new Vector3(6.5f, 1.5f, 10.5f), Vector3.UnitX));
    }

    [Fact]
    public void HoverEvaluation_AllocatesNothing_OverAnEnemyAndOverEmptyGround()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(8.5f, 1.5f, 10.5f)));
        Vector3 origin = new(6.5f, 1.5f, 10.5f);
        Vector3 away = new(5.5f, 10f, 5.5f);
        int hits = 0;

        AllocationAssert.AtMost(0, () =>
        {
            hits += WorldCursorPicker.PickHoveredEnemy(world, registry, origin, Vector3.UnitX) is not null ? 1 : 0;
            hits += WorldCursorPicker.PickHoveredEnemy(world, registry, away, -Vector3.UnitY) is not null ? 1 : 0;
        });

        Assert.True(hits > 0);
    }
}
