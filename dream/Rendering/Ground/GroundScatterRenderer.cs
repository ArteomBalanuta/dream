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

    public GroundScatterRenderer(Render render)
    {
        _render = render;
    }

    public GroundScatterIndex Index => _index;

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
                Shader shader = _render.GetShader(info.Layer <= GroundLayer.Cover ? TerrainShaderName : ActorShaderName);
                model.BeginRender(shader, view, projection, enableCulling: false);
                model.RenderPreparedInstanced(shader, _matrices, count);
                model.EndRender();
                LastDrawnCount += count;
                LastDrawCalls++;
            }

            cursor = end;
        }
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
