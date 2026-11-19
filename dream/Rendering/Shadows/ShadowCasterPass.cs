using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Rendering.Shadows;

// What the caster pass drives. Render supplies the GL implementation (RenderShadowPassTarget in Render.Shadows.cs); the
// tests supply a recording one, which is how "an exception mid-pass still restores" is proved without a GL context.
public interface IShadowPassTarget
{
    // Binds the sun map as the depth target over its whole extent and clears it.
    void BeginSun();

    // Binds the atlas, restricts the viewport to the tile and clears exactly that tile.
    void BeginFireTile(int slot, int face);

    // Draws every caster the view accepts into the current target, depth only.
    void DrawCasters(in ShadowCasterView view);

    // Restores the framebuffer, viewport and fixed-function state. Idempotent, and always called.
    void End();
}

// What one pass submitted, measured with RenderCounters around each target (the existing instrumentation). Fire draws are
// the total over every face of every slot; the per-face average is FireDraws / FireFaces.
public readonly record struct ShadowPassStats(int SunDraws, int FireDraws, int FireFaces)
{
    public int TotalDraws => SunDraws + FireDraws;

    public float DrawsPerFireFace => FireFaces == 0 ? 0f : (float)FireDraws / FireFaces;
}

// The shadow caster pass: renders geometry INTO the sun map and the fire atlas. Nothing here samples either map (the
// composition is workstream D3's), so a frame with the pass on is pixel-identical to one with it off, which is exactly
// what makes a leaked viewport, framebuffer or uniform in THIS pass unambiguous.
//
// State discipline: ShadowMapTargets saves the framebuffer, viewport, depth test/mask/func, cull and blend on the first
// Begin* and restores them in End; this pass never duplicates that. It calls End in a finally, so a throw mid-pass cannot
// leave the window rendering into a 512 x 512 tile. Everything else a caster draw touches is on the shadowDepth program
// (view, projection, the skinning and instancing flags, uWind) or is reset by its owner in a finally (GroundScatterRenderer's
// sway), and no other program, texture binding or VAO is left changed.
public static class ShadowCasterPass
{
    // ON since integration: the receivers sample both maps (lighting.glsl's dreamSunVisibility with
    // DREAM_SUN_SHADOW_LIVE = 1, and fireShadows.glsl's dreamFireVisibility through pointLights.glsl). While it was false
    // the sun strength and every fire slot's strength were zero (the shaders returned visibility 1.0) and the ground-blob
    // shadows stayed on, so the frame was pixel-identical to the frame before the pass existed. It is the single switch
    // between "maps written, unread" and "maps read"; DREAM_SHADOWS=0 still turns the whole system off and the blobs back on.
    public static readonly bool MapsAreSampled = true;

    public static float Strength => MapsAreSampled ? 1f : 0f;

    // The ground blobs (FxShadowList) are the fallback: they draw whenever the maps are not the shadow source, which is
    // when DREAM_SHADOWS=0 or the receivers do not sample the maps yet. The sun map reaches only ~23 units from the focus.
    public static bool ShadowMapsReplaceBlobs => ShadowSettings.Enabled && MapsAreSampled;

    /// <summary>
    /// Renders the sun map and then every fire tile of <paramref name="frame"/>. The camera is the frame's (the casters
    /// that need one for plumbing take it; culling is the light's, never the camera's). Returns what it submitted.
    /// </summary>
    public static ShadowPassStats Render(Render render, in ShadowFrame frame, Camera camera, Vector3 focus)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(camera);
        IShadowPassTarget? target = render.ShadowPassTarget(camera);
        return target is null ? default : Run(target, in frame, focus);
    }

    private static readonly GpuDebugLabel SunMapGroup = new("Sun shadow map");
    private static readonly GpuDebugLabel FireAtlasGroup = new("Fire shadow atlas");

    /// <summary>The pass proper, against any target. The sun map first, then slot-major, face-major over the fire tiles.</summary>
    public static ShadowPassStats Run(IShadowPassTarget target, in ShadowFrame frame, Vector3 focus)
    {
        ArgumentNullException.ThrowIfNull(target);
        int sunDraws = 0;
        int fireDraws = 0;
        int fireFaces = 0;
        try
        {
            // A frame built by ForSun has a positive texel; ShadowFrame.None (no light, or shadows off) has none. The test
            // is not SunActive, because the strength stays 0 until the maps are sampled while the map is still rendered.
            if (frame.SunTexelWorld > 0f)
            {
                using GpuDebugScope sunGroup = GpuDebug.Scope(SunMapGroup);
                target.BeginSun();
                Matrix4 identity = Matrix4.Identity;
                ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);
                ShadowCasterView view = new(in identity, in frame.SunLightViewProjection, in frustum, focus);
                int before = RenderCounters.DrawCalls;
                target.DrawCasters(in view);
                sunDraws = RenderCounters.DrawCalls - before;
            }

            ReadOnlySpan<FireShadowSlot> fires = frame.Fires;
            int count = Math.Clamp(frame.FireCount, 0, fires.Length);
            using GpuDebugScope fireGroup = GpuDebug.Scope(FireAtlasGroup);
            for (int slot = 0; slot < count; slot++)
            {
                FireShadowSlot fire = fires[slot];
                if (!(fire.Far > fire.Near) || !float.IsFinite(fire.Far) || !float.IsFinite(fire.Position.X + fire.Position.Y + fire.Position.Z))
                {
                    continue;
                }

                Matrix4 projection = FireShadowAtlas.FaceProjection(fire.Near, fire.Far);
                for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
                {
                    target.BeginFireTile(slot, face);
                    Matrix4 faceView = FireShadowAtlas.FaceView(fire.Position, face);
                    ViewFrustum frustum = ShadowCasterSelection.FireFaceFrustum(in faceView, in projection);
                    ShadowCasterView view = new(in faceView, in projection, in frustum, focus, fire.CarrierActorId);
                    int before = RenderCounters.DrawCalls;
                    target.DrawCasters(in view);
                    fireDraws += RenderCounters.DrawCalls - before;
                    fireFaces++;
                }
            }
        }
        finally
        {
            target.End();
        }

        return new ShadowPassStats(sunDraws, fireDraws, fireFaces);
    }
}

// The skinned actors a renderer drew last frame, kept for the caster pass. The shadow pass runs before the actors draw
// (the sun matrix has to be in the lighting block before the world pass), so it replays what each actor renderer recorded
// on its previous Draw: the same model, the same AnimatedModelInstance (whose pose is therefore the previous frame's, so
// the skinning is the SAME skinning the visible pass used, limb for limb) and the same model matrix and pose overrides.
// The shadow trails the actor by exactly one frame; at the frame rates this renders at that is a couple of centimetres
// of travel. Entries are cleared at the start of every Draw and again when the pass has consumed them, so an actor that
// stopped drawing cannot leave a ghost shadow, and nothing is recorded at all when shadows are off.
public sealed class ShadowActorCasters
{
    private readonly record struct Entry(
        AnimatedModel Model,
        AnimatedModelInstance Instance,
        Matrix4 ModelMatrix,
        IReadOnlyDictionary<string, Matrix4>? Overrides,
        int OwnerActorId);

    private readonly List<Entry> _entries = new();

    public int Count => _entries.Count;

    public void Clear() => _entries.Clear();

    public void Add(
        AnimatedModel model,
        AnimatedModelInstance instance,
        in Matrix4 modelMatrix,
        IReadOnlyDictionary<string, Matrix4>? overrides = null,
        int ownerActorId = 0)
    {
        if (!ShadowSettings.Enabled)
        {
            return;
        }

        _entries.Add(new Entry(model, instance, modelMatrix, overrides, ownerActorId));
    }

    /// <summary>
    /// Whether a recorded actor stays out of a view: the one that carries the view's fire. Owner 0 is "no owner" (enemies,
    /// NPCs) and an exclusion of 0 is "none", so neither ever matches.
    /// </summary>
    public static bool IsExcluded(int ownerActorId, int excludeOwner) => excludeOwner != 0 && ownerActorId == excludeOwner;

    /// <summary>Draws the recorded actors the view accepts, depth only. Returns how many were drawn.</summary>
    public int Draw(Render render, in ShadowCasterView view)
    {
        if (_entries.Count == 0)
        {
            return 0;
        }

        Shader shader = render.GetShader(Render.ShadowDepthShaderName);
        int drawn = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (IsExcluded(entry.OwnerActorId, view.ExcludeOwner))
            {
                continue;
            }

            (Vector3 min, Vector3 max) = ShadowCasterSelection.ActorBounds(entry.ModelMatrix);
            if (!view.Accepts(min, max))
            {
                continue;
            }

            entry.Model.BeginDepthRender(shader, view.View, view.Projection);
            try
            {
                entry.Model.RenderPrepared(shader, entry.Instance, entry.ModelMatrix, entry.Overrides);
            }
            finally
            {
                entry.Model.EndRender();
            }

            drawn++;
        }

        return drawn;
    }
}
