using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.Props;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ground;

// Draws the ground scatter: one instanced draw per asset that has anything in view. Decals and cover use the "cube"
// program (lit like the terrain they lie on); accents and landmarks use "enemy" (the actors' lit program, with the
// frame's point lights). Nothing here collides or blocks. The models load in the background through ModelAssets; an
// asset whose model is not ready is skipped this frame. DREAM_GROUND_SCATTER=0 turns it off, for A/B captures.
public sealed class GroundScatterRenderer
{
    public const string EnabledEnvironmentVariable = "DREAM_GROUND_SCATTER";

    public const float DecalDrawDistance = 24f;
    public const float CoverDrawDistance = 24f;
    public const float AccentDrawDistance = 34f;
    public const float LandmarkDrawDistance = 42f;

    private const string TerrainShaderName = "cube";
    private const string ActorShaderName = "enemy";

    private readonly bool _enabled = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable) != "0";
    private readonly Render _render;
    private readonly GroundScatterIndex _index = new();
    private Matrix4[] _matrices = new Matrix4[256];
    private bool[] _cellVisible = [];
    private GroundAssetSet? _assets;
    private GroundAssetSet? _preloaded;
    private readonly WindUniform _terrainWind;
    private readonly WindUniform _actorWind;
    // The shadow caster pass's own state: its matrices and its cell visibility. The sway tracker of the shadowDepth program
    // is Render.ShadowWind, owned with the program (this class keeps one tracker per program it writes in its own file).
    private Matrix4[] _casterMatrices = new Matrix4[256];
    private bool[] _casterCellVisible = [];

    public GroundScatterRenderer(Render render)
    {
        _render = render;
        _terrainWind = new WindUniform(wind => ApplyWind(_render.GetShader(TerrainShaderName), wind));
        _actorWind = new WindUniform(wind => ApplyWind(_render.GetShader(ActorShaderName), wind));
    }

    public GroundScatterIndex Index => _index;

    public const string WindUniformName = "uWind";

    // Sets uWind on the program and on its alpha-mask variant (masked leaves draw with the variant).
    private static void ApplyWind(Shader shader, Vector2 wind)
    {
        shader.Use();
        shader.SetVector2(WindUniformName, wind);
        if (shader.AlphaMaskVariant is { } maskProgram)
        {
            maskProgram.Use();
            maskProgram.SetVector2(WindUniformName, wind);
            shader.Use();
        }
    }

    // The same writer for the shadowDepth program (Render.ShadowWind), so its sway reaches the program and its MASK variant by
    // exactly the code that reaches cube and enemy.
    internal static void ApplyShadowWind(Shader shader, Vector2 wind) => ApplyWind(shader, wind);

    // Instances and instanced draws the last Draw submitted (tests and diagnostics).
    public int LastDrawnCount { get; private set; }

    public int LastDrawCalls { get; private set; }

    // After PropIndex.Update, once per frame. The shipped manifests load once, on the first call.
    public void Update(PropIndex props, GroundAssetSet? assets = null)
    {
        if (!_enabled)
        {
            return;
        }

        _assets ??= assets ?? GroundAssetSet.LoadShipped();
        _index.Update(props, _assets);
    }

    public void Draw(Dream.Simulation.Players.Camera camera, Vector3 focus)
    {
        LastDrawnCount = 0;
        LastDrawCalls = 0;
        IReadOnlyList<GroundPlacement> placements = _index.Placements;
        if (!_enabled || placements.Count == 0)
        {
            return;
        }

        IReadOnlyList<GroundAsset> assets = _index.Assets.Assets;
        if (!ReferenceEquals(_preloaded, _index.Assets))
        {
            string[] paths = new string[assets.Count];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = assets[i].ModelPath;
            }

            ModelAssets.Preload(paths);
            _preloaded = _index.Assets;
        }

        Matrix4 view = camera.GetViewMatrix();
        Matrix4 projection = _render.ProjectionMatrix;
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(view, projection);
        IReadOnlyList<GroundScatterIndex.Block> blocks = _index.Blocks;

        // Visibility once per spatial cell per frame; every asset's pass then only looks inside the visible cells.
        IReadOnlyList<GroundScatterIndex.Cell> cells = _index.Cells;
        if (_cellVisible.Length < cells.Count)
        {
            _cellVisible = new bool[cells.Count];
        }

        for (int c = 0; c < cells.Count; c++)
        {
            GroundScatterIndex.Cell cell = cells[c];
            _cellVisible[c] = CellVisible(in cell, LandmarkDrawDistance, focus, in frustum);
        }

        AnimatedModel? drawnModel = null;
        try
        {
            int cursor = 0;
            while (cursor < blocks.Count)
            {
                // Blocks are sorted by asset, so each asset is one contiguous run of blocks and one instanced draw.
                int asset = blocks[cursor].Asset;
                int end = cursor;
                while (end < blocks.Count && blocks[end].Asset == asset)
                {
                    end++;
                }

                GroundAsset info = assets[asset];
                int count = 0;
                for (int b = cursor; b < end; b++)
                {
                    GroundScatterIndex.Block block = blocks[b];
                    if (!_cellVisible[block.Cell])
                    {
                        continue;
                    }

                    for (int i = block.Start; i < block.Start + block.Count; i++)
                    {
                        GroundPlacement p = placements[i];
                        if (!IsVisible(in p, info, focus, in frustum))
                        {
                            continue;
                        }

                        if (count == _matrices.Length)
                        {
                            Array.Resize(ref _matrices, count * 2);
                        }

                        _matrices[count++] =
                            Matrix4.CreateScale(p.Scale) *
                            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(p.YawDegrees)) *
                            Matrix4.CreateTranslation(p.Position);
                    }
                }

                if (count > 0 && ModelAssets.TryGet(info.ModelPath, out AnimatedModel? model))
                {
                    bool terrain = info.Layer <= GroundLayer.Cover;
                    Shader shader = _render.GetShader(terrain ? TerrainShaderName : ActorShaderName);
                    // WindFor is shared with DrawCasters: the shadow of a canopy sways with exactly this value.
                    Vector2 wind = WindFor(info);
                    (terrain ? _terrainWind : _actorWind).Set(wind);
                    model.Wind = wind;
                    drawnModel = model;
                    model.BeginRender(shader, view, projection, enableCulling: false);
                    model.RenderPreparedInstanced(shader, _matrices, count);
                    model.EndRender();
                    model.Wind = Vector2.Zero;
                    drawnModel = null; // only a model mid-draw can be left dirty
                    LastDrawnCount += count;
                    LastDrawCalls++;
                }

                cursor = end;
            }
        }
        finally
        {
            // Never leak the sway: the cube and enemy programs also draw the terrain and every actor.
            _terrainWind.Reset();
            _actorWind.Reset();
            // ModelAssets hands out shared models: an exception mid-draw must not leave a sway on one.
            if (drawnModel != null)
            {
                drawnModel.Wind = Vector2.Zero;
            }
        }
    }

    // The uWind value an asset is drawn with: the ONE definition both Draw and DrawCasters use, so the shadow of a
    // swaying plant is displaced by the same amount as the plant (ShadowWindParityTests pins both call sites).
    public static Vector2 WindFor(GroundAsset asset) => GroundWind.Uniform(asset.Class, asset.Height);

    // The shadow caster counterpart of Draw: the same placements, the same sway, drawn depth-only into the target the view
    // describes. Decals are skipped (flat stains on the ground, and MASK, so they cast nothing); everything else casts. The
    // sway goes to the shadowDepth program and its MASK variant exactly as Draw sends it to cube / enemy, and it is reset in
    // a finally on every path, because that program also draws the terrain, the props and every actor's shadow.
    // Returns the number of instances submitted.
    public int DrawCasters(in Dream.Rendering.Shadows.ShadowCasterView view)
    {
        IReadOnlyList<GroundPlacement> placements = _index.Placements;
        if (!_enabled || placements.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<GroundAsset> assets = _index.Assets.Assets;
        IReadOnlyList<GroundScatterIndex.Block> blocks = _index.Blocks;
        IReadOnlyList<GroundScatterIndex.Cell> cells = _index.Cells;
        if (_casterCellVisible.Length < cells.Count)
        {
            _casterCellVisible = new bool[cells.Count];
        }

        ViewFrustum frustum = view.Frustum;
        for (int c = 0; c < cells.Count; c++)
        {
            GroundScatterIndex.Cell cell = cells[c];
            _casterCellVisible[c] = CellVisible(in cell, LandmarkDrawDistance, view.Focus, in frustum);
        }

        Shader shader = _render.GetShader(Render.ShadowDepthShaderName);
        int submitted = 0;
        AnimatedModel? drawnModel = null;
        try
        {
            int cursor = 0;
            while (cursor < blocks.Count)
            {
                int asset = blocks[cursor].Asset;
                int end = cursor;
                while (end < blocks.Count && blocks[end].Asset == asset)
                {
                    end++;
                }

                GroundAsset info = assets[asset];
                if (info.Layer != GroundLayer.Decal)
                {
                    int count = 0;
                    for (int b = cursor; b < end; b++)
                    {
                        GroundScatterIndex.Block block = blocks[b];
                        if (!_casterCellVisible[block.Cell])
                        {
                            continue;
                        }

                        for (int i = block.Start; i < block.Start + block.Count; i++)
                        {
                            GroundPlacement p = placements[i];
                            CasterBounds(in p, info, out Vector3 min, out Vector3 max, out float reach);
                            if (!view.Accepts(min, max, reach))
                            {
                                continue;
                            }

                            if (count == _casterMatrices.Length)
                            {
                                Array.Resize(ref _casterMatrices, count * 2);
                            }

                            _casterMatrices[count++] =
                                Matrix4.CreateScale(p.Scale) *
                                Matrix4.CreateRotationY(MathHelper.DegreesToRadians(p.YawDegrees)) *
                                Matrix4.CreateTranslation(p.Position);
                        }
                    }

                    if (count > 0 && ModelAssets.TryGet(info.ModelPath, out AnimatedModel? model))
                    {
                        Vector2 wind = WindFor(info);
                        _render.ShadowWind.Set(wind);
                        model.Wind = wind;
                        drawnModel = model;
                        model.BeginDepthRender(shader, view.View, view.Projection);
                        model.RenderPreparedInstanced(shader, _casterMatrices, count);
                        model.EndRender();
                        model.Wind = Vector2.Zero;
                        drawnModel = null;
                        submitted += count;
                    }
                }

                cursor = end;
            }
        }
        finally
        {
            _render.ShadowWind.Reset();
            if (drawnModel != null)
            {
                drawnModel.Wind = Vector2.Zero;
                drawnModel.EndRender();
            }
        }

        return submitted;
    }

    // IsVisible's box, and the distance a caster of this placement is accepted at: its layer's draw distance (plus its
    // radius) capped at the caster distance, so a plant the camera never draws does not cast either.
    public static void CasterBounds(in GroundPlacement placement, GroundAsset asset, out Vector3 min, out Vector3 max, out float reach)
    {
        float radius = asset.Radius * placement.Scale;
        Vector3 p = placement.Position;
        min = new Vector3(p.X - radius, p.Y - 0.3f, p.Z - radius);
        max = new Vector3(p.X + radius, p.Y + (asset.Height * placement.Scale) + 0.3f, p.Z + radius);
        reach = MathF.Min(Dream.Rendering.Shadows.ShadowCasterSelection.CasterDistance, DrawDistance(asset.Layer) + radius);
    }

    public static float DrawDistance(GroundLayer layer) => layer switch
    {
        GroundLayer.Decal => DecalDrawDistance,
        GroundLayer.Cover => CoverDrawDistance,
        GroundLayer.Accent => AccentDrawDistance,
        _ => LandmarkDrawDistance,
    };

    // Within the layer's draw distance of the focus (plus the asset's own radius) and its coarse box touches the frustum.
    public static bool IsVisible(in GroundPlacement placement, GroundAsset asset, Vector3 focus, in ViewFrustum frustum)
    {
        float radius = asset.Radius * placement.Scale;
        float reach = DrawDistance(asset.Layer) + radius;
        float dx = placement.Position.X - focus.X;
        float dz = placement.Position.Z - focus.Z;
        if ((dx * dx) + (dz * dz) > reach * reach)
        {
            return false;
        }

        Vector3 p = placement.Position;
        return frustum.IntersectsAabb(
            new Vector3(p.X - radius, p.Y - 0.3f, p.Z - radius),
            new Vector3(p.X + radius, p.Y + (asset.Height * placement.Scale) + 0.3f, p.Z + radius));
    }

    // The whole spatial cell: the nearest point of its box to the focus is within the distance, and the box touches the frustum.
    public static bool CellVisible(in GroundScatterIndex.Cell cell, float distance, Vector3 focus, in ViewFrustum frustum)
    {
        float dx = MathF.Max(0f, MathF.Max(cell.Min.X - focus.X, focus.X - cell.Max.X));
        float dz = MathF.Max(0f, MathF.Max(cell.Min.Z - focus.Z, focus.Z - cell.Max.Z));
        return ((dx * dx) + (dz * dz) <= distance * distance) && frustum.IntersectsAabb(cell.Min, cell.Max);
    }
}
