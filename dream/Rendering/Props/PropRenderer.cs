using Dream.Rendering.Core;
using Dream.Rendering.Models;
using OpenTK.Mathematics;

namespace Dream.Rendering.Props;

// Draws the world props with the actors' lit program ("enemy": ambient + sun + the frame's point lights, then
// exposure and fog), so a brazier bowl and the walls round it respond to the brazier's own light. The props are
// rigid models with a baked albedo atlas and a contact-occlusion disc under them (an alpha-MASK primitive, which
// the program's MASK variant discards cleanly: blending is off in the world pass). No framebuffer object, no new
// GL state, no sorting. The models load in the background through ModelAssets; a prop whose model is not ready
// yet is skipped this frame and appears when it is.
public sealed class PropRenderer
{
    // Props farther than this from the camera focus (plus their own radius) are not drawn: the iso view shows
    // about 25 units across, and ground this far out is mostly fog at night.
    public const float DrawDistance = 42f;

    private const string ShaderName = "enemy";

    // DREAM_WORLD_PROPS=0 turns the scenery off, for A/B captures against the bare terrain. Read once.
    public const string EnabledEnvironmentVariable = "DREAM_WORLD_PROPS";

    private readonly bool _enabled = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable) != "0";

    private readonly Render _render;
    private readonly PropIndex _index = new();
    private readonly Dream.Rendering.Ground.GroundScatterRenderer _ground;
    private readonly int[] _visibleByKind = new int[PropCatalog.KindCount];
    private bool _preloaded;

    public PropRenderer(Render render)
    {
        _render = render;
        _ground = new Dream.Rendering.Ground.GroundScatterRenderer(render);
    }

    public PropIndex Index => _index;

    // Props the last Draw submitted (tests and diagnostics).
    public int LastDrawnCount { get; private set; }

    // The ground scatter this renderer feeds and draws.
    public Dream.Rendering.Ground.GroundScatterRenderer Scatter => _ground;

    // After CollectFrameLights, once per frame.
    public void Update(in Dream.Simulation.World.Geometry.GeometryRenderExport export, Dream.Rendering.Lighting.PlacedLightIndex placedLights)
    {
        _index.Update(in export, placedLights);
        _ground.Update(_index);
    }

    public void Draw(Dream.Simulation.Players.Camera camera, Vector3 focus)
    {
        // The floor clutter first (DREAM_WORLD_PROPS does not gate it; DREAM_GROUND_SCATTER does), before the early return.
        _ground.Draw(camera, focus);

        IReadOnlyList<PropPlacement> placements = _index.Placements;
        LastDrawnCount = 0;
        if (placements.Count == 0 || !_enabled)
        {
            return;
        }

        if (!_preloaded)
        {
            string[] paths = new string[PropCatalog.KindCount];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = PropCatalog.ModelPath((PropKind)i);
            }

            ModelAssets.Preload(paths);
            _preloaded = true;
        }

        Matrix4 view = camera.GetViewMatrix();
        Matrix4 projection = _render.ProjectionMatrix;
        // The heavy ones (an oak is 62k triangles) must not be submitted for ground that is off screen: the
        // distance cull alone keeps every prop within 42 units, which is several screens in the isometric view.
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(view, projection);
        Array.Clear(_visibleByKind);
        for (int i = 0; i < placements.Count; i++)
        {
            if (IsVisible(placements[i], focus, in frustum))
            {
                _visibleByKind[(int)placements[i].Kind]++;
            }
        }

        // Props never sway this wave (a swaying cottage is wrong; gates and shutters want per-prop authoring).
        // uWind is already 0 here: _ground.Draw resets it in a finally, and props draw non-instanced besides.
        Shader shader = _render.GetShader(ShaderName);
        int cursor = 0;
        while (cursor < placements.Count)
        {
            // Placements are sorted by kind, so each kind is one contiguous run and one BeginRender.
            PropKind kind = placements[cursor].Kind;
            int end = cursor;
            while (end < placements.Count && placements[end].Kind == kind)
            {
                end++;
            }

            if (_visibleByKind[(int)kind] > 0 && ModelAssets.TryGet(PropCatalog.ModelPath(kind), out AnimatedModel? model))
            {
                model.BeginRender(shader, view, projection, enableCulling: false);
                for (int i = cursor; i < end; i++)
                {
                    PropPlacement p = placements[i];
                    if (!IsVisible(p, focus, in frustum))
                    {
                        continue;
                    }

                    Matrix4 matrix =
                        Matrix4.CreateScale(p.Scale) *
                        Matrix4.CreateRotationY(MathHelper.DegreesToRadians(p.YawDegrees)) *
                        Matrix4.CreateTranslation(p.Position);
                    model.RenderPrepared(shader, matrix);
                    LastDrawnCount++;
                }

                model.EndRender();
            }

            cursor = end;
        }
    }

    // Within the draw distance of the focus, and the prop's coarse box touches the view frustum.
    public static bool IsVisible(in PropPlacement placement, Vector3 focus, in ViewFrustum frustum)
    {
        PropBounds bounds = PropCatalog.Bounds(placement.Kind);
        float radius = bounds.HorizontalRadius * placement.Scale;
        float reach = DrawDistance + radius;
        float dx = placement.Position.X - focus.X;
        float dz = placement.Position.Z - focus.Z;
        if ((dx * dx) + (dz * dz) > reach * reach)
        {
            return false;
        }

        Vector3 p = placement.Position;
        return frustum.IntersectsAabb(
            new Vector3(p.X - radius, p.Y - 0.3f, p.Z - radius),
            new Vector3(p.X + radius, p.Y + (bounds.Height * placement.Scale) + 0.3f, p.Z + radius));
    }
}
