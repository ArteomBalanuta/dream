using OpenTK.Mathematics;
using Dream.Client.Navigation;
using Dream.Simulation.Enemies;

namespace Dream.Client.Input;

// What the cursor points at in the world. Pure ray maths plus a cell march, so it is testable with
// a fake INavWorld. Runs on presses and held repaths, and once per frame for the hover probe, which takes
// the allocation-free PickHoveredEnemy path.
public static class WorldCursorPicker
{
    public const float EnemyMaxDistance = 90f;
    public const float EnemyHitRadius = 1f;
    public const float MarchStepMetres = 0.05f;
    public const float MarchMaxMetres = 60f;
    private const float FloorTop = 1f;

    // Ray from the inverse view-projection of the camera. Row-vector matrices, as OpenTK uses them.
    public static bool TryBuildRay(
        Matrix4 view,
        Matrix4 projection,
        Vector2 pixel,
        Vector2i viewport,
        out Vector3 origin,
        out Vector3 direction)
    {
        origin = default;
        direction = default;
        if (viewport.X <= 0 || viewport.Y <= 0 || !float.IsFinite(pixel.X) || !float.IsFinite(pixel.Y))
        {
            return false;
        }

        Matrix4 viewProjection = view * projection;
        if (MathF.Abs(viewProjection.Determinant) < 1e-12f)
        {
            return false;
        }

        Matrix4 inverse = Matrix4.Invert(viewProjection);
        float ndcX = (2f * pixel.X / viewport.X) - 1f;
        float ndcY = 1f - (2f * pixel.Y / viewport.Y);
        if (!TryUnproject(inverse, ndcX, ndcY, -1f, out Vector3 near) || !TryUnproject(inverse, ndcX, ndcY, 1f, out Vector3 far))
        {
            return false;
        }

        Vector3 delta = far - near;
        if (delta.LengthSquared < 1e-10f)
        {
            return false;
        }

        origin = near;
        direction = delta.Normalized();
        return true;
    }

    private static bool TryUnproject(Matrix4 inverse, float x, float y, float z, out Vector3 point)
    {
        Vector4 p = Vector4.TransformRow(new Vector4(x, y, z, 1f), inverse);
        if (MathF.Abs(p.W) < 1e-9f)
        {
            point = default;
            return false;
        }

        point = p.Xyz / p.W;
        return true;
    }

    // March the ray to its first blocked cell, then let an enemy win only if it stands in front of
    // that cell; otherwise the answer is the last empty cell before it.
    public static ClickPick Pick(
        INavWorld world,
        Vector3 origin,
        Vector3 direction,
        Func<Vector3, Vector3, IClickTarget?>? resolveEnemy)
    {
        bool haveEmpty = false;
        NavCell lastEmpty = default;
        ClickPick result = default;
        float wallDistance = float.PositiveInfinity;
        bool done = false;
        int steps = (int)(MarchMaxMetres / MarchStepMetres);
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = origin + (direction * (i * MarchStepMetres));
            int x = (int)MathF.Floor(p.X);
            int y = (int)MathF.Floor(p.Y);
            int z = (int)MathF.Floor(p.Z);
            if (x < 0 || y < 0 || z < 0 || x >= world.Width || y >= world.Height || z >= world.Depth)
            {
                continue;
            }

            if (world.IsBlocked(x, y, z))
            {
                wallDistance = i * MarchStepMetres;
                result = haveEmpty ? new ClickPick(ClickPickKind.Ground, lastEmpty, p, null) : default;
                done = true;
                break;
            }

            haveEmpty = true;
            lastEmpty = new NavCell(x, y, z);
        }

        if (resolveEnemy?.Invoke(origin, direction) is { } enemy
            && Vector3.Dot(enemy.Anchor - origin, direction) <= wallDistance)
        {
            return new ClickPick(ClickPickKind.Enemy, default, enemy.Anchor, enemy);
        }

        return done ? result : EdgePick(world, origin, direction);
    }

    // Past the edge of the map nothing stops the ray, so aim for the ground cell nearest to where
    // it crosses the floor: the character walks to the edge rather than ignoring the click.
    private static ClickPick EdgePick(INavWorld world, Vector3 origin, Vector3 direction)
    {
        if (direction.Y >= 0f)
        {
            return default;
        }

        Vector3 p = origin + (direction * ((FloorTop - origin.Y) / direction.Y));
        int x = Math.Clamp((int)MathF.Floor(p.X), 0, world.Width - 1);
        int z = Math.Clamp((int)MathF.Floor(p.Z), 0, world.Depth - 1);
        return new ClickPick(ClickPickKind.Ground, new NavCell(x, (int)FloorTop, z), p, null);
    }

    // Distance along the ray to the first blocked cell (the same march Pick makes), or infinity when the
    // ray leaves the map without meeting one.
    public static float WallDepth(INavWorld world, Vector3 origin, Vector3 direction)
    {
        int steps = (int)(MarchMaxMetres / MarchStepMetres);
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = origin + (direction * (i * MarchStepMetres));
            int x = (int)MathF.Floor(p.X);
            int y = (int)MathF.Floor(p.Y);
            int z = (int)MathF.Floor(p.Z);
            if (x < 0 || y < 0 || z < 0 || x >= world.Width || y >= world.Height || z >= world.Depth)
            {
                continue;
            }

            if (world.IsBlocked(x, y, z))
            {
                return i * MarchStepMetres;
            }
        }

        return float.PositiveInfinity;
    }

    // The live enemy a click on this ray would target, or null. Same rule as Pick with the registry
    // overload (an enemy wins only in front of the first wall) but allocates nothing: no closure,
    // delegate or target object, so the per-frame hover probe can run it every frame.
    public static EnemyActor? PickHoveredEnemy(INavWorld world, EnemyRegistry enemies, Vector3 origin, Vector3 direction)
    {
        float wallDistance = WallDepth(world, origin, direction);
        EnemyActor? hit = enemies.TryResolveHit(origin, direction, EnemyMaxDistance, EnemyHitRadius);
        if (hit is null || !EnemyClickTarget.IsLive(hit, enemies))
        {
            return null;
        }

        return Vector3.Dot(hit.Combatant.GetCombatAnchorWorldPosition() - origin, direction) <= wallDistance
            ? hit
            : null;
    }

    public static ClickPick Pick(
        INavWorld world,
        EnemyRegistry enemies,
        Vector3 origin,
        Vector3 direction)
    {
        return Pick(world, origin, direction, (o, d) =>
        {
            EnemyActor? hit = enemies.TryResolveHit(o, d, EnemyMaxDistance, EnemyHitRadius);
            return hit is null ? null : new EnemyClickTarget(hit, enemies);
        });
    }
}

// An enemy actor as a click target; the anchor is read live, so a moving enemy is followed. The client
// never marks a killed enemy dead (a snapshot just stops listing it and the registry drops it), so the
// target counts as alive only while the registry still holds this very actor.
public sealed class EnemyClickTarget : IClickTarget
{
    private readonly EnemyActor _actor;
    private readonly EnemyRegistry? _registry;

    public EnemyClickTarget(EnemyActor actor, EnemyRegistry? registry = null)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _registry = registry;
    }

    public bool IsAlive => IsLive(_actor, _registry);

    // Active, and (with a registry) still the very actor the registry holds.
    internal static bool IsLive(EnemyActor actor, EnemyRegistry? registry) =>
        actor.IsActive
        && (registry is null
            || (registry.TryGetEnemyByActorId(actor.Combatant.Actor.Id, out EnemyActor? current) && ReferenceEquals(current, actor)));

    public Vector3 Anchor => _actor.Combatant.GetCombatAnchorWorldPosition();
}
