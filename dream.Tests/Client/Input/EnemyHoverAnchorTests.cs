using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Rendering.Core;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Input;

// The hover probe now returns the hovered enemy's combat anchor, because the cursor aim steers by it
// (CursorAimParallaxTests says why). Two things have to hold for that to be safe: the anchor must be
// the one a real click would attack, and getting it must stay free of per-frame allocation - the probe
// runs every frame, and the play scene reaches it through an override, not the interface default.
public sealed class EnemyHoverAnchorTests
{
    private sealed class FloorWorld : INavWorld
    {
        public int Width => 32;

        public int Height => 16;

        public int Depth => 32;

        public bool IsBlocked(int x, int y, int z) => y == 0;

        public bool IsSupport(int x, int y, int z) => IsBlocked(x, y, z);
    }

    private static EnemyActor Wolf(int actorId, Vector3 position)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: 80);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f);
    }

    // The two paths the play scene really has: TryPick is the click's full pick, and the override is the
    // allocation-free one the probe takes. Both come from WorldCursorPicker, as they do in PlayScene.
    private sealed class Host : IClickMoveHost
    {
        private readonly INavWorld _world;
        private readonly EnemyRegistry _enemies;
        private readonly Vector3 _origin;
        private readonly Vector3 _direction;

        public Host(INavWorld world, EnemyRegistry enemies, Vector3 origin, Vector3 direction)
        {
            _world = world;
            _enemies = enemies;
            _origin = origin;
            _direction = direction;
        }

        public INavWorld? World => _world;

        public Vector3 BodyCentre => _origin;

        public Vector3 Eye => _origin;

        public bool NoclipActive => false;

        public PlayerInteractionMode Mode => PlayerInteractionMode.Combat;

        public Vector3 Facing => Vector3.UnitX;

        public bool AimOwnsFacing => false;

        public bool TryPick(Vector2 screenPosition, out ClickPick pick)
        {
            pick = WorldCursorPicker.Pick(_world, _enemies, _origin, _direction);
            return pick.Kind != ClickPickKind.None;
        }

        public bool TryGetEnemyAnchorAt(Vector2 screenPosition, out Vector3 anchor)
        {
            anchor = default;
            EnemyActor? hovered = WorldCursorPicker.PickHoveredEnemy(_world, _enemies, _origin, _direction);
            if (hovered is null)
            {
                return false;
            }

            anchor = hovered.Combatant.GetCombatAnchorWorldPosition();
            return true;
        }

        public void SetFacing(Vector3 direction) { }
    }

    private static WorldPointerState Pointer() => new(new Vector2(640f, 360f), false, false, false);

    [Fact]
    public void TheAnchorTheProbeReports_IsTheOneAClickWouldAttack()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(8.5f, 1.5f, 10.5f)));
        Vector3 origin = new(6.5f, 1.5f, 10.5f);
        Host host = new(world, registry, origin, Vector3.UnitX);

        Assert.True(new EnemyHoverProbe(host).Evaluate(Pointer(), true, true, out Vector3 anchor));

        Assert.True(host.TryPick(Vector2.Zero, out ClickPick click));
        Assert.Equal(ClickPickKind.Enemy, click.Kind);
        Assert.NotNull(click.Target);
        Assert.Equal(click.Target!.Anchor, anchor);
    }

    [Fact]
    public void NoEnemyUnderTheCursor_ReportsNoAnchor()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(8.5f, 1.5f, 20.5f)));
        Host host = new(world, registry, new Vector3(6.5f, 1.5f, 10.5f), Vector3.UnitX);

        Assert.False(new EnemyHoverProbe(host).Evaluate(Pointer(), true, true, out Vector3 anchor));
        Assert.Equal(Vector3.Zero, anchor);
    }

    // The gate that matters for the frame budget, measured through the probe rather than on the picker
    // in isolation: this is the call the play scene makes, and it is the one that would silently fall
    // back to the allocating default if the override were dropped.
    [Fact]
    public void TheProbePath_AllocatesNothing_OverAnEnemyAndOverEmptyGround()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(8.5f, 1.5f, 10.5f)));
        EnemyHoverProbe onEnemy = new(new Host(world, registry, new Vector3(6.5f, 1.5f, 10.5f), Vector3.UnitX));
        EnemyHoverProbe onGround = new(new Host(world, registry, new Vector3(5.5f, 10f, 5.5f), -Vector3.UnitY));
        WorldPointerState pointer = Pointer();

        // Stated once, here, rather than counted inside the measured block: AllocationAssert runs the
        // delegate at least 164 times, so any count taken in there is a function of its own loop. What
        // matters is that these two really are the hit and the miss, so the measurement below is of a
        // probe doing its work and not of one short-circuiting.
        Assert.True(onEnemy.Evaluate(pointer, true, true, out _));
        Assert.False(onGround.Evaluate(pointer, true, true, out _));

        AllocationAssert.AtMost(0, () =>
        {
            onEnemy.Evaluate(pointer, true, true, out _);
            onGround.Evaluate(pointer, true, true, out _);
        });
    }

    // And that the shipped scene really takes that override, which no unit test can reach: PlayScene
    // owns a GLFW window. Without this line the probe falls to the interface default, which allocates a
    // closure, a delegate and a target object on every frame, and nothing above would notice.
    [Fact]
    public void ThePlayScene_ServesTheAnchorFromTheAllocationFreePick()
    {
        string scene = ReadRepoFile("dream", "Client", "Scenes", "PlayScene.cs");
        int method = scene.IndexOf("public bool TryGetEnemyAnchorAt(", StringComparison.Ordinal);
        Assert.True(method > 0, "PlayScene must override TryGetEnemyAnchorAt, not just IsEnemyAt");
        // Bounded to the override's own body, or a later method's use of the same picker would do.
        int nextMethod = scene.IndexOf("\n        public ", method + 1, StringComparison.Ordinal);
        Assert.True(nextMethod > method);
        string body = scene[method..nextMethod];
        Assert.Contains("WorldCursorPicker.PickHoveredEnemy(", body, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "dream", "Rendering")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root! }.Concat(parts).ToArray()));
    }
}
