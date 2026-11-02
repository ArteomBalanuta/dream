using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Client.Input;

public class WorldCursorPickerTests
{
    private sealed class FloorWorld : INavWorld
    {
        public int Width => 32;

        public int Height => 16;

        public int Depth => 32;

        public bool IsBlocked(int x, int y, int z) => y == 0 || (x == 10 && y == 1 && z == 10);

        public bool IsSupport(int x, int y, int z) => IsBlocked(x, y, z);
    }

    [Fact]
    public void RayDownOntoTheFloor_ReturnsTheLastEmptyCell()
    {
        ClickPick pick = WorldCursorPicker.Pick(new FloorWorld(), new Vector3(5.5f, 10f, 5.5f), -Vector3.UnitY, null);

        Assert.Equal(ClickPickKind.Ground, pick.Kind);
        Assert.Equal(new NavCell(5, 1, 5), pick.Cell);
    }

    [Fact]
    public void RayOntoACubeSide_ReturnsTheCellBesideIt()
    {
        Vector3 origin = new(6.5f, 1.5f, 10.5f);
        ClickPick pick = WorldCursorPicker.Pick(new FloorWorld(), origin, Vector3.UnitX, null);

        Assert.Equal(ClickPickKind.Ground, pick.Kind);
        Assert.Equal(new NavCell(9, 1, 10), pick.Cell);
    }

    [Fact]
    public void RayIntoTheSky_PicksNothing()
    {
        ClickPick pick = WorldCursorPicker.Pick(new FloorWorld(), new Vector3(5.5f, 10f, 5.5f), Vector3.UnitY, null);

        Assert.Equal(ClickPickKind.None, pick.Kind);
    }

    [Fact]
    public void AnEnemyOnTheRayWinsOverTheFloor()
    {
        FakeTarget enemy = new();
        ClickPick pick = WorldCursorPicker.Pick(new FloorWorld(), new Vector3(5.5f, 10f, 5.5f), -Vector3.UnitY, (o, d) => enemy);

        Assert.Equal(ClickPickKind.Enemy, pick.Kind);
        Assert.Same(enemy, pick.Target);
    }

    [Fact]
    public void AnEnemyBehindACube_LosesTheClick_ButOneInFrontKeepsIt()
    {
        Vector3 origin = new(6.5f, 1.5f, 10.5f);

        ClickPick hidden = WorldCursorPicker.Pick(new FloorWorld(), origin, Vector3.UnitX, (o, d) => new FakeTarget(new Vector3(12.5f, 1.5f, 10.5f)));
        ClickPick visible = WorldCursorPicker.Pick(new FloorWorld(), origin, Vector3.UnitX, (o, d) => new FakeTarget(new Vector3(8.5f, 1.5f, 10.5f)));

        Assert.Equal(ClickPickKind.Ground, hidden.Kind);
        Assert.Equal(new NavCell(9, 1, 10), hidden.Cell);
        Assert.Equal(ClickPickKind.Enemy, visible.Kind);
    }

    [Fact]
    public void TheCentreOfTheScreen_UnprojectsToTheCameraForwardRay()
    {
        Matrix4 view = Matrix4.LookAt(new Vector3(0f, 10f, 0f), new Vector3(0f, 0f, 0f), -Vector3.UnitZ);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(35f), 16f / 9f, 1f, 100f);

        bool ok = WorldCursorPicker.TryBuildRay(view, projection, new Vector2(800f, 450f), new Vector2i(1600, 900), out Vector3 origin, out Vector3 direction);

        Assert.True(ok);
        Assert.True(direction.Y < -0.999f);
        Assert.InRange(origin.Y, 8.9f, 9.1f);
    }

    [Fact]
    public void ADegenerateViewport_BuildsNoRay()
    {
        Assert.False(WorldCursorPicker.TryBuildRay(Matrix4.Identity, Matrix4.Identity, Vector2.Zero, new Vector2i(0, 0), out _, out _));
    }

    private sealed class FakeTarget(Vector3? anchor = null) : IClickTarget
    {
        public bool IsAlive => true;

        public Vector3 Anchor => anchor ?? new(5.5f, 1.5f, 5.5f);
    }

    private static EnemyActor Wolf(int actorId, Vector3 position)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: 80);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f);
    }

    [Fact]
    public void EnemyClickTarget_IsAliveWhileTheRegistryHoldsTheActor_AndNotOnceASnapshotDroppedIt()
    {
        // Regression: the client never sets a killed enemy's health to 0 (the snapshot stops listing it
        // and the registry drops it), so the actor object kept IsActive and an attack goal never ended.
        EnemyRegistry registry = new();
        EnemyActor wolf = Wolf(2000, new Vector3(10.5f, 1.5f, 10.5f));
        registry.Add(wolf);
        EnemyClickTarget target = new(wolf, registry);
        Assert.True(target.IsAlive);

        registry.RemoveAllExcept(new HashSet<int>());

        Assert.True(wolf.IsActive);
        Assert.False(target.IsAlive);
    }

    [Fact]
    public void EnemyClickTarget_ARespawnWithTheSameIdIsNotTheClickedEnemy()
    {
        EnemyRegistry registry = new();
        EnemyActor first = Wolf(2000, new Vector3(10.5f, 1.5f, 10.5f));
        registry.Add(first);
        EnemyClickTarget target = new(first, registry);
        registry.Clear();
        registry.Add(Wolf(2000, new Vector3(10.5f, 1.5f, 10.5f)));

        Assert.False(target.IsAlive);
    }

    [Fact]
    public void EnemyClickTarget_AHealthZeroActorIsNotAlive_AndTheAnchorFollowsTheActor()
    {
        EnemyRegistry registry = new();
        EnemyActor wolf = Wolf(2001, new Vector3(4.5f, 1.5f, 4.5f));
        registry.Add(wolf);
        EnemyClickTarget target = new(wolf, registry);
        Vector3 before = target.Anchor;

        wolf.SetPosition(new Vector3(8.5f, 1.5f, 4.5f));
        Assert.Equal(before.X + 4f, target.Anchor.X, 3);

        wolf.Combatant.Health.SetAuthoritativeState(0, 80);
        Assert.False(target.IsAlive);
    }
}
