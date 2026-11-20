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

        Assert.True(host.TryGetEnemyAnchorAt(Vector2.Zero, out Vector3 anchor));

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

        Assert.False(host.TryGetEnemyAnchorAt(Vector2.Zero, out Vector3 anchor));
        Assert.Equal(Vector3.Zero, anchor);
    }

    // And that the shipped scene really overrides it, which no unit test can reach: PlayScene owns a GLFW
    // window. The override serves the world hover's answer (WorldHover, picked once and throttled), so it
    // never falls to the interface default, which runs a full pick and allocates a closure, a delegate and a
    // target object, and it never picks a second time on its own.
    [Fact]
    public void ThePlayScene_ServesTheAnchorFromTheWorldHover()
    {
        string scene = ReadRepoFile("dream", "Client", "Scenes", "PlayScene.cs");
        int method = scene.IndexOf("public bool TryGetEnemyAnchorAt(", StringComparison.Ordinal);
        Assert.True(method > 0, "PlayScene must override TryGetEnemyAnchorAt, not just IsEnemyAt");
        // Bounded to the override's own body, or a later method's use of the same picker would do.
        int nextMethod = scene.IndexOf("\n        public ", method + 1, StringComparison.Ordinal);
        Assert.True(nextMethod > method);
        string body = scene[method..nextMethod];
        Assert.Contains("TryGetHoveredEnemy(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPick(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("WorldCursorPicker.", body, StringComparison.Ordinal);
    }

    // A right click must attack exactly the enemy the cursor shows: TryPick takes the world hover's enemy and
    // otherwise makes a ground pick with no enemy resolution of its own (which could disagree with the hover).
    [Fact]
    public void ThePlayScene_ClickPick_TakesTheHoveredEnemy_AndNoOtherEnemy()
    {
        string scene = ReadRepoFile("dream", "Client", "Scenes", "PlayScene.cs");
        int method = scene.IndexOf("public bool TryPick(", StringComparison.Ordinal);
        Assert.True(method > 0);
        int nextMethod = scene.IndexOf("\n        public ", method + 1, StringComparison.Ordinal);
        Assert.True(nextMethod > method);
        string body = scene[method..nextMethod];
        Assert.Contains("TryGetHoveredEnemy(", body, StringComparison.Ordinal);
        Assert.Contains("resolveEnemy: null", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_scene._runtime.Enemies, origin", body, StringComparison.Ordinal);
    }

    // The hover is updated before anything reads it in the same update, and a press forces a fresh pick, so
    // the click and the aim never act on a throttled answer from before the press.
    [Fact]
    public void ThePlayScene_UpdatesTheHover_BeforeTheAimAndTheClick_AndForcesAPickOnAPress()
    {
        string update = ReadRepoFile("dream", "Client", "Scenes", "PlayScene.Update.cs");
        int hover = update.IndexOf("UpdateWorldHover(", StringComparison.Ordinal);
        int aim = update.IndexOf("_cursorAim.OnFrame(", StringComparison.Ordinal);
        int click = update.IndexOf("_clickMove.OnFrame(", StringComparison.Ordinal);
        Assert.True(hover > 0 && aim > hover && click > hover, "UpdateWorldHover must run before the aim and the click");

        string wiring = ReadRepoFile("dream", "Client", "Scenes", "PlayScene.Hover.cs");
        Assert.Contains("pointer.LeftDown && !_hoverLeftWasDown", wiring, StringComparison.Ordinal);
        Assert.Contains("pointer.RightDown && !_hoverRightWasDown", wiring, StringComparison.Ordinal);
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
