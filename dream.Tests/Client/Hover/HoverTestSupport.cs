using Dream.Client.Hover;
using Dream.Client.Navigation;
using Dream.Client.Presentation;
using Dream.Rendering.Ui;
using Dream.Simulation.Enemies;
using OpenTK.Mathematics;

namespace Dream.Tests.Client.Hover;

// Shared fakes for the world hover tests: a floor world, a camera looking down -Z at head height, a pose
// source the test can move, and a counting source.
internal static class HoverTestSupport
{
    public static readonly Vector2i Viewport = new(800, 600);
    public static readonly Vector2 Centre = new(400f, 300f);

    // Eye at (10.5, 2, 20.5) looking along -Z: the centre pixel's ray runs at y = 2 down the line x = 10.5.
    public static Matrix4 View => Matrix4.LookAt(new Vector3(10.5f, 2f, 20.5f), new Vector3(10.5f, 2f, 10.5f), Vector3.UnitY);

    public static Matrix4 Projection => Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver3, 800f / 600f, 0.1f, 200f);

    public static HoverUpdate Frame(
        INavWorld? world,
        float dt = 1f / 60f,
        Vector2? pixel = null,
        bool gated = false,
        bool overUi = false,
        HoverTargetId label = default,
        bool force = false,
        bool camera = true,
        Matrix4? view = null) =>
        new(dt, gated, overUi, label, world, camera, view ?? View, Projection, pixel ?? Centre, Viewport, force);

    public static EnemyActor Wolf(int actorId, Vector3 feet, float radius = 0.45f, float height = 1.2f)
    {
        EnemyCombatant combatant = new(() => feet, actorId: actorId, maxHealth: 80);
        return new EnemyActor(combatant, feet, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f, collisionRadius: radius, collisionHeight: height);
    }

    public sealed class FloorWorld : INavWorld
    {
        public HashSet<(int X, int Y, int Z)> Blocks { get; } = new();

        public int Width => 32;

        public int Height => 16;

        public int Depth => 32;

        public bool IsBlocked(int x, int y, int z) => y == 0 || Blocks.Contains((x, y, z));

        public bool IsSupport(int x, int y, int z) => IsBlocked(x, y, z);
    }

    public sealed class PoseSource : IEnemyRenderPoseSource
    {
        public Dictionary<int, Vector3> Feet { get; } = new();

        public bool TryGetEnemyRenderPose(int actorId, double renderTick, out InterpolatedActorPose pose)
        {
            if (Feet.TryGetValue(actorId, out Vector3 feet))
            {
                pose = new InterpolatedActorPose(feet, -Vector3.UnitZ);
                return true;
            }

            pose = default;
            return false;
        }
    }

    // Reports a fixed hit whenever the ray is asked, and counts the asks.
    public sealed class CountingSource(HoverKind kind, long id, float depth) : IHoverSource
    {
        public int TryHitCalls { get; private set; }

        public bool Live { get; set; } = true;

        public bool Hits { get; set; } = true;

        public Vector3 Anchor { get; set; } = new(1f, 2f, 3f);

        public HoverKind Kind { get; } = kind;

        public long Id { get; } = id;

        public float Depth { get; set; } = depth;

        public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit)
        {
            TryHitCalls++;
            hit = new HoverHit(new HoverTargetId(Kind, Id), Depth, Anchor);
            return Hits && Live;
        }

        public bool TryGetLive(HoverTargetId id, out Vector3 anchor)
        {
            anchor = Anchor;
            return Live && id.Kind == Kind && id.Id == Id;
        }
    }
}
