using Dream.Rendering.Ground;
using Dream.Rendering.Lighting;
using Dream.Rendering.Models;
using Dream.Rendering.Shadows;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

// Real shadow mapping, the render side (docs/ledger/wave-shadow-maps.md): the shadow frame, the sun map and the fire atlas,
// and the caster pass that fills them. DREAM_SHADOWS=0 is the kill switch: no maps are allocated beyond a 1x1 dummy, no pass
// runs, the frame is ShadowFrame.None and the ground-blob shadows (FxShadowList) stay the only shadows.
public partial class Render
{
    private ShadowMapTargets? _shadowTargets;
    private ShadowFrame _shadowFrame;
    // The frame's export, for the dynamic cubes and blocks. Set for the duration of the caster pass only.
    private GeometryRenderExport _shadowExport;
    private RenderShadowPassTarget? _shadowPassTarget;
    private ShadowInstanceDrawTarget? _shadowInstanceTarget;

    // The maps (null before InitializeShadows and after disposal; Enabled false under DREAM_SHADOWS=0).
    public ShadowMapTargets? ShadowTargets => _shadowTargets;

    // The shadow description of the frame being drawn: the sun matrix and parameters (also written into the lighting block)
    // and the fire slots the atlas was rendered for. ShadowFrame.None when shadows are off.
    public ref readonly ShadowFrame CurrentShadowFrame => ref _shadowFrame;

    // What the last caster pass submitted, measured with RenderCounters (draw calls, per target kind).
    public ShadowPassStats LastShadowPassStats { get; private set; }

    public void SetShadowFrame(in ShadowFrame frame) => _shadowFrame = frame;

    // The uWind tracker of the shadowDepth program (and its MASK variant), the counterpart of GroundScatterRenderer's trackers for
    // cube and enemy. Only GroundScatterRenderer.DrawCasters sets it, and resets it in a finally.
    private WindUniform? _shadowWind;

    internal WindUniform ShadowWind => _shadowWind ??= new WindUniform(
        wind => GroundScatterRenderer.ApplyShadowWind(GetShader(ShadowDepthShaderName), wind));

    // Builds this frame's ShadowFrame, writes its sun half into the lighting block and renders both maps.
    //
    // WHERE IT SITS IN THE FRAME: after the scene has collected the point lights (PlayScene.CollectFrameLights) and set the
    // frame lighting, and BEFORE WorldRenderer.DrawWorld uploads the lighting. Both orderings are forced. The fire slots are
    // chosen from the frame's selected LightList, which only exists once the lights are collected, so a pass any earlier
    // would shadow last frame's flame; and the world pass must find the finished maps and the sun matrix already in the block
    // it uploads (UploadFrameLighting is pinned to one call site inside DrawWorld, so the sun matrix is written to the
    // struct here rather than moving the upload). Re-selecting the lights below is idempotent: UploadFrameLighting runs the
    // same selection on the same inputs, a 16-slot insertion.
    //
    // The actors replay what they drew last frame (ShadowActorCasters), so the shadow of a moving figure trails it by one frame.
    public void PrepareShadows(Camera camera, Vector3 focus, in GeometryRenderExport export)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!ShadowSettings.Enabled || _shadowTargets is not { Enabled: true } targets)
        {
            _shadowFrame = ShadowFrame.None;
            LastShadowPassStats = default;
            return;
        }

        Vector3 cameraPosition = FogOrigin ?? camera.Position;
        Matrix4 view = camera.GetViewMatrix();
        SelectPointLights(cameraPosition, in view);

        float strength = ShadowCasterPass.Strength;
        ShadowFrame frame = BuildSunFrame(in _frameLighting, focus, strength, ShadowSettings.SunResolution);
        FireShadowAtlas.FillSlots(in _selectedLights, focus, ShadowSettings.CastingFireCount, strength, ref frame, _selectedLightCarriers);
        SetShadowFrame(in frame);
        frame.WriteSunTo(ref _frameLighting);

        // The depth program is cubeVertex.glsl, whose wind sway reads the sway clock (uFrameTime.x) from the FrameLighting
        // block, and the block still holds LAST frame's clock: DrawWorld uploads it only after this pass. Uploading the frame's
        // lighting here too (DrawWorld's upload follows with identical contents) makes the shadow of a swaying canopy use the
        // clock the canopy itself is drawn with. It goes to the buffer directly, not through UploadFrameLighting, whose single
        // call site in DrawWorld stays as pinned.
        _frameLightingBuffer?.Upload(in _frameLighting, cameraPosition, in view, FxClockSeconds());

        _shadowExport = export;
        try
        {
            LastShadowPassStats = ShadowCasterPass.Render(this, in _shadowFrame, camera, focus);
        }
        finally
        {
            // The actors' recordings are consumed: an actor that does not draw next frame must not cast.
            _shadowExport = default;
            _enemyRenderer.ClearCasters();
            _localPlayerRenderer.ClearCasters();
            _remotePlayerRenderer.ClearCasters();
            _npcModelRenderer?.ClearCasters();
        }

        targets.BindForSampling();
    }

    /// <summary>
    /// The dominant light's shadow volume, or <see cref="ShadowFrame.None"/> when no light is up. Dominance is the shader's own
    /// rule (lighting.glsl dreamSunVisibility): the sun while its intensity is at least the moon's, else the moon.
    /// </summary>
    internal static ShadowFrame BuildSunFrame(in FrameLighting lighting, Vector3 focus, float strength, int resolution)
    {
        Vector4 light = lighting.SunDirection.W >= lighting.MoonDirection.W ? lighting.SunDirection : lighting.MoonDirection;
        if (!(light.W > 0f) || !(light.Y > 0f))
        {
            return ShadowFrame.None;
        }

        return ShadowFrame.ForSun(focus, light.Xyz, light.Y, resolution, strength);
    }

    private void InitializeShadows()
    {
        _shadowTargets = new ShadowMapTargets();
        _shadowTargets.BindForSampling();
        _shadowPassTarget = new RenderShadowPassTarget(this);
        _shadowInstanceTarget = new ShadowInstanceDrawTarget(this);
        Console.WriteLine(ShadowSettings.Enabled
            ? $"[startup] shadows: on, sun map {ShadowSettings.SunResolution}, fire atlas {ShadowSettings.AtlasResolution}, " +
              $"{ShadowSettings.CastingFireCount} casting fires (DREAM_SHADOWS, DREAM_SHADOW_RESOLUTION, DREAM_SHADOW_FIRES)"
            : "[startup] shadows: off (DREAM_SHADOWS=0), ground blobs only");
    }

    private void DisposeShadows()
    {
        _shadowPassTarget = null;
        _shadowInstanceTarget = null;
        _shadowTargets?.Dispose();
        _shadowTargets = null;
    }

    // The GL half of the caster pass, or null when there is nothing to render into.
    internal IShadowPassTarget? ShadowPassTarget(Camera camera)
    {
        if (_shadowTargets is not { Enabled: true } || _shadowPassTarget is null)
        {
            return null;
        }

        _shadowPassTarget.Camera = camera;
        return _shadowPassTarget;
    }

    // Binds the shadowDepth program for a world-space draw (model = identity): the static chunks and the ground items.
    // Skinning and instancing are off; uWind is whatever its owner left, which is zero outside GroundScatterRenderer.DrawCasters.
    public Shader BeginShadowDepthProgram(in Matrix4 view, in Matrix4 projection)
    {
        Shader shader = GetShader(ShadowDepthShaderName);
        shader.Use();
        shader.SetModelMatrix(Matrix4.Identity);
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", projection);
        shader.SetBool("useSkinning", false);
        shader.SetBool("useInstancing", false);
        return shader;
    }

    // Everything that casts, into the target the view describes. Statics first (the biggest set), then the dynamic cubes
    // and blocks, the props and ground scatter, the skinned actors and the ground items.
    internal void DrawShadowCasters(Camera camera, in ShadowCasterView view)
    {
        _worldRenderer.DrawCasters(camera, in view, in _shadowExport);
        _propRenderer.DrawCasters(in view);
        _enemyRenderer.DrawCasters(in view);
        _localPlayerRenderer.DrawCasters(in view);
        _remotePlayerRenderer.DrawCasters(in view);
        _npcModelRenderer?.DrawCasters(in view);
        DrawShadowGroundItems(in view);
    }

    internal IWorldInstanceDrawTarget ShadowInstanceTarget(in ShadowCasterView view)
    {
        ShadowInstanceDrawTarget target = _shadowInstanceTarget ?? throw new InvalidOperationException("Shadows are not initialised.");
        target.View = view.View;
        target.Projection = view.Projection;
        return target;
    }

    // The pass's GL target: ShadowMapTargets does the framebuffer, viewport and fixed-function state; this forwards.
    internal sealed class RenderShadowPassTarget : IShadowPassTarget
    {
        private readonly Render _render;

        public RenderShadowPassTarget(Render render) => _render = render;

        public Camera? Camera { get; set; }

        public void BeginSun() => Targets.BeginSun();

        public void BeginFireTile(int slot, int face) => Targets.BeginFireTile(slot, face);

        public void DrawCasters(in ShadowCasterView view) => _render.DrawShadowCasters(Camera!, in view);

        public void End() => _render._shadowTargets?.End();

        private ShadowMapTargets Targets => _render._shadowTargets
            ?? throw new InvalidOperationException("Shadow maps are not initialised.");
    }

    // The depth-only counterpart of Render's own instanced draws (IWorldInstanceDrawTarget): the dynamic cubes and blocks,
    // grouped by the unchanged WorldRenderBatchPlanner, drawn with the shadowDepth program and no material.
    private sealed class ShadowInstanceDrawTarget : IWorldInstanceDrawTarget
    {
        private readonly Render _render;

        public ShadowInstanceDrawTarget(Render render) => _render = render;

        public Matrix4 View { get; set; }

        public Matrix4 Projection { get; set; }

        void IWorldInstanceDrawTarget.DrawModelInstances(Camera camera, AnimatedModel animation, string shaderName, Matrix4[] modelMatrices, int count)
        {
            if (count == 0)
            {
                return;
            }

            Shader shader = _render.GetShader(ShadowDepthShaderName);
            animation.BeginDepthRender(shader, View, Projection);
            try
            {
                animation.RenderPreparedInstanced(shader, modelMatrices, count);
            }
            finally
            {
                animation.EndRender();
            }
        }

        void IWorldInstanceDrawTarget.DrawFallbackInstances(Camera camera, GeometryMaterialId materialId, Matrix4[] modelMatrices, int count)
        {
            if (count == 0)
            {
                return;
            }

            Shader shader = _render.BeginShadowDepthProgram(View, Projection);
            shader.SetBool("useInstancing", true);
            try
            {
                _render._fallbackInstances.Upload(modelMatrices, count);
                _render._fallbackCubeBuffer.DrawInstanced(_render._fallbackInstances, count);
            }
            finally
            {
                shader.SetBool("useInstancing", false);
            }
        }
    }
}
