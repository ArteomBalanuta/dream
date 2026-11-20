using OpenTK.Mathematics;
using Dream.Client.Replication;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.Shadows;
using Dream.Simulation.Players;

namespace Dream.Rendering.Actors;

// How one villager looks: the forged model, its clips and the scale that model measured for itself.
// AuthoredScale is a delegate for the same reason as EnemyVisual's: a forged model measures its
// authored height from the GLB on first use, which must not run in a static field initializer.
public readonly record struct NpcVisual(
    string ModelPath,
    string IdleClip,
    string WalkClip,
    Func<float> AuthoredScale)
{
    public float ResolveScale() => AuthoredScale();
}

// The client's table of villager looks, keyed by ClientNpc.VisualKey. The wire value is hostile: an
// unknown, null or oversized key draws the default vendor, the same length-first pattern as
// EnemyVisualCatalog.Resolve. Allocates nothing.
public static class NpcVisualCatalog
{
    public const string VendorKey = "npc.vendor";
    public const string SmithKey = "npc.smith";
    public const string WardenKey = "npc.warden";

    // Static so a Resolve never allocates a closure.
    private static readonly Func<float> VendorScale = static () => NpcAssetDefinition.VendorVisualScale;
    private static readonly Func<float> SmithScale = static () => NpcAssetDefinition.SmithVisualScale;
    private static readonly Func<float> WardenScale = static () => NpcAssetDefinition.WardenVisualScale;

    private static readonly NpcVisual Vendor = new(
        NpcAssetDefinition.VendorModelPath, NpcAssetDefinition.IdleClip, NpcAssetDefinition.WalkClip, VendorScale);

    private static readonly NpcVisual Smith = new(
        NpcAssetDefinition.SmithModelPath, NpcAssetDefinition.IdleClip, NpcAssetDefinition.WalkClip, SmithScale);

    private static readonly NpcVisual Warden = new(
        NpcAssetDefinition.WardenModelPath, NpcAssetDefinition.IdleClip, NpcAssetDefinition.WalkClip, WardenScale);

    // What an unknown key draws, and the vendor's own look.
    public static NpcVisual Default => Vendor;

    public static NpcVisual Resolve(string? visualKey)
    {
        if (visualKey is null)
        {
            return Vendor;
        }

        // Length first so an oversized key is rejected without a compare.
        if (visualKey.Length == SmithKey.Length &&
            string.Equals(visualKey, SmithKey, StringComparison.Ordinal))
        {
            return Smith;
        }

        if (visualKey.Length == WardenKey.Length &&
            string.Equals(visualKey, WardenKey, StringComparison.Ordinal))
        {
            return Warden;
        }

        return Vendor;
    }
}

// The village NPCs as forged animated models instead of WorldEntityRenderer's two coloured boxes
// (M5). Shaped exactly like EnemyRenderer: poll ModelAssets.TryGet, keep one AnimatedModelInstance
// per actor id in a keyed cache, advance it once per frame by this frame's dt, prune the ids that
// left. The pose comes from ClientNpcCache (position and facing are already replicated), so there
// is no protocol or server change and no pose source to plumb. The villagers are untinted, and they
// say so themselves: uActorTintOffset is one uniform shared with the enemy draws, so this class
// uploads zero to it once per frame instead of relying on EnemyRenderer's reset still running
// before it (an undeclared cross-class invariant on shared GL state, invisible to any test).
public sealed class NpcModelRenderer
{
    private readonly Render _render;
    private readonly EnemyVisualStateCache _visualStates = new();
    private readonly HashSet<int> _currentIds = new();
    private readonly ShadowActorCasters _casters = new();

    public NpcModelRenderer(Render render)
    {
        ArgumentNullException.ThrowIfNull(render);
        _render = render;
    }

    // The villagers the last Draw put on screen, drawn depth-only into a shadow map (see ShadowActorCasters).
    public int DrawCasters(in ShadowCasterView view) => _casters.Draw(_render, in view);

    public void ClearCasters() => _casters.Clear();

    public int DrawnCount { get; private set; }

    // Zero in the base program and the MASK variant (the shared uActorTintOffset).
    private static void ZeroTint(Shader shader)
    {
        shader.Use();
        shader.SetVector3(TintUniformName, Vector3.Zero);
        if (shader.AlphaMaskVariant is { } maskProgram)
        {
            maskProgram.Use();
            maskProgram.SetVector3(TintUniformName, Vector3.Zero);
            shader.Use();
        }
    }

    public void Draw(Camera camera, ClientNpcCache npcs, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(npcs);

        _currentIds.Clear();
        _casters.Clear();
        DrawnCount = 0;

        // Zero the shared tint once, before the loop: whatever the enemy pass left in
        // uActorTintOffset (the Dire Wolf's -0.45 dark offset, say) would otherwise darken every
        // villager if its reset were skipped or the passes were reordered. The MASK variant reads
        // the per-model value, which is already zero on a villager model.
        Shader shader = _render.GetShader(ShaderName);
        ZeroTint(shader);

        for (int n = 0; n < npcs.Count; n++)
        {
            ClientNpc npc = npcs[n];
            if (ClientGroundItemCache.HorizontalDistanceSquared(camera.Position, npc.Position) > DrawDistanceSquared)
            {
                continue;
            }

            _currentIds.Add(npc.ActorId);
            NpcVisual visual = NpcVisualCatalog.Resolve(npc.VisualKey);

            // The shared model loads in the background; until it is ready the villager is not drawn
            // and no clip is recorded, so the clip is applied on the first drawn frame.
            if (!ModelAssets.TryGet(visual.ModelPath, out AnimatedModel? model))
            {
                continue;
            }

            AnimatedModelInstance instance = EnemyRenderer.AdvanceInstance(
                _visualStates.GetOrAdd(npc.ActorId, model.Skeleton),
                // Villagers stand where the server puts them; the walk clip waits for a walking NPC.
                visual.IdleClip,
                deltaTime);

            Vector3 tint = ActorHighlight.NpcTint(HighlightActorId, npc.ActorId);
            bool lifted = !tint.Equals(Vector3.Zero);
            model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
            if (lifted)
            {
                // The base program's uniform, and the model's value that BindProgram carries to the MASK variant.
                model.ActorTintOffset = tint;
                shader.SetVector3(TintUniformName, tint);
            }

            Matrix4 modelMatrix = BuildModelMatrix(in visual, npc.Position, npc.Facing);
            model.RenderPrepared(shader, instance, modelMatrix);
            model.EndRender();
            if (lifted)
            {
                ZeroTint(shader);
                model.ActorTintOffset = Vector3.Zero;
            }

            _casters.Add(model, instance, modelMatrix);
            DrawnCount++;
        }

        _visualStates.PruneExcept(_currentIds);
    }

    // The villager the world hover rests on (replicated actor id): drawn with ActorHighlight.Lift, every
    // other villager with zero, and zero is put back right after it.
    public int? HighlightActorId { get; set; }

    // The same draw distance as the ground-item and (former) NPC boxes.
    public const float DrawDistance = WorldEntityRenderer.DrawDistance;

    public const string ShaderName = "enemy";

    // The same uniform EnemyRenderer tints with; the villagers hold it at zero.
    public const string TintUniformName = "uActorTintOffset";

    private const float DrawDistanceSquared = DrawDistance * DrawDistance;

    // Scale, then the yaw that turns the authored forward toward the replicated facing, then the
    // position. The model's origin is at the feet, like the replicated NPC position.
    public static Matrix4 BuildModelMatrix(in NpcVisual visual, Vector3 position, Vector3 facing)
    {
        Vector3 resolved = facing.LengthSquared > 0.0001f ? facing.Normalized() : -Vector3.UnitZ;
        float yawRadians = MathF.Atan2(resolved.X, resolved.Z) -
            MathF.Atan2(NpcAssetDefinition.AuthoredForwardAxis.X, NpcAssetDefinition.AuthoredForwardAxis.Z);

        return Matrix4.CreateScale(visual.ResolveScale()) *
            Matrix4.CreateRotationY(yawRadians) *
            Matrix4.CreateTranslation(position + NpcAssetDefinition.BodyCenterToFootOffset);
    }
}
