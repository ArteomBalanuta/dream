using Dream.Rendering.Props;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ground;

public readonly record struct GroundPlacement(int Asset, Vector3 Position, float YawDegrees, float Scale);

// Where the ground assets lie: a pure function of the replicated static ground, the prop placements and the asset
// set - no RNG, no clock, no seed (the world seed is server-only), so every client derives the same list. Client
// scenery only: nothing here collides, is simulated or is persisted.
//
// Four layers, placed in this order so the big pieces claim ground first:
//   landmark - one candidate per 7x7 bucket (35%), trees; kept out of the authored zone and away from the spawn.
//   accent   - one candidate per 3x3 bucket (28%), bushes, boulders, stumps, logs.
//   cover    - up to two per cell (30% on grass, 12% elsewhere), knee-high clutter, mushrooms thick near trees.
//   decal    - one candidate per cell (7%), flat stains on level ground that do not overlap each other.
// A low-frequency noise field (12-cell lattice) scales the cover and decal densities so they clump. Every layer
// stays 2 cells off the map edge and out of the cells a prop footprint or an accent/landmark radius covers; those also keep off
// the roads and keep their radii apart from each other and from the props. Output is sorted by asset, x, z.
public static class GroundScatterLayout
{
    public const int EdgeCells = 2;
    public const int LandmarkBucket = 7;
    public const int AccentBucket = 3;
    public const float SpawnClearance = 2.5f;
    public const float DecalLiftA = 0.008f;
    public const float DecalLiftB = 0.012f;
    public const float PropMargin = 0.3f;

    private const float SpawnLane = 5.5f;
    private const float SpawnX = 5f;
    private const float SpawnZ = 5f;
    private const int ClumpCells = 12;
    private const float MushroomBoost = 3f;
    private const float MushroomReach = 3f;
    private static readonly int MaterialCount = (int)Enum.GetValues<GeometryMaterialId>().Max() + 1;

    private const uint ClumpSalt = 0xC1A4F;
    private const uint DecalSalt = 0xD3CA1;
    private const uint CoverSalt = 0xC0FE2;
    private const uint AccentSalt = 0xACC37;
    private const uint LandmarkSalt = 0x1A4D5;

    private static readonly Comparison<GroundPlacement> ByAssetThenPosition = static (a, b) =>
    {
        int c = a.Asset.CompareTo(b.Asset);
        if (c != 0)
        {
            return c;
        }

        c = a.Position.X.CompareTo(b.Position.X);
        if (c != 0)
        {
            return c;
        }

        c = a.Position.Z.CompareTo(b.Position.Z);
        return c != 0 ? c : a.Position.Y.CompareTo(b.Position.Y);
    };

    public static void Build(StaticGround ground, IReadOnlyList<PropPlacement> props, GroundAssetSet assets, List<GroundPlacement> output)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        if (assets.Assets.Count == 0 || ground.ColumnCount == 0)
        {
            return;
        }

        new Run(ground, props, assets.Assets, output).Execute();
        output.Sort(ByAssetThenPosition);
    }

    private readonly record struct Solid(float X, float Z, float Radius);

    private sealed class Run
    {
        private readonly StaticGround _ground;
        private readonly IReadOnlyList<PropPlacement> _props;
        private readonly IReadOnlyList<GroundAsset> _assets;
        private readonly List<GroundPlacement> _output;
        private readonly int _width;
        private readonly int _depth;
        private readonly int[][] _suit;
        private readonly bool[] _occupied;
        private readonly bool[] _nearHost;
        private readonly List<Solid> _solids = [];

        public Run(StaticGround ground, IReadOnlyList<PropPlacement> props, IReadOnlyList<GroundAsset> assets, List<GroundPlacement> output)
        {
            _ground = ground;
            _props = props;
            _assets = assets;
            _output = output;
            _width = ground.MaxX + 1;
            _depth = ground.MaxZ + 1;
            _suit = BuildSuit(assets);
            _occupied = new bool[_width * _depth];
            _nearHost = new bool[_width * _depth];
        }

        public void Execute()
        {
            MarkProps();
            PlaceBig(GroundLayer.Landmark, LandmarkBucket, 35u, 0.5f, LandmarkSalt);
            PlaceBig(GroundLayer.Accent, AccentBucket, 28u, 0.25f, AccentSalt);
            MarkBigFootprints();
            MarkHosts();
            PlaceCover();
            PlaceDecals();
        }

        private static int[][] BuildSuit(IReadOnlyList<GroundAsset> assets)
        {
            int[][] suit = new int[4 * MaterialCount][];
            List<int> scratch = [];
            for (int layer = 0; layer < 4; layer++)
            {
                for (int material = 0; material < MaterialCount; material++)
                {
                    scratch.Clear();
                    for (int i = 0; i < assets.Count; i++)
                    {
                        if ((int)assets[i].Layer == layer && assets[i].Ground.Contains((GeometryMaterialId)material))
                        {
                            scratch.Add(i);
                        }
                    }

                    suit[(layer * MaterialCount) + material] = scratch.ToArray();
                }
            }

            return suit;
        }

        private int Index(int x, int z) => (z * _width) + x;

        private bool Usable(int x, int z, out StaticGround.Column column) =>
            _ground.TryGet(x, z, out column) && !column.Blocked && column.Material != GeometryMaterialId.Lamp &&
            (uint)column.Material < (uint)MaterialCount;

        private bool NearEdge(int x, int z) => x < EdgeCells || z < EdgeCells || x > _ground.MaxX - EdgeCells || z > _ground.MaxZ - EdgeCells;

        // Cells any part of which could lie within a prop's footprint grown by PropMargin: the cell centre is tested
        // against the footprint grown by the cell's half diagonal as well, so no point of a marked-clear cell is inside.
        private void MarkProps()
        {
            const float halfDiagonal = 0.7072f;
            for (int i = 0; i < _props.Count; i++)
            {
                PropPlacement p = _props[i];
                PropFootprint fp = PropCatalog.Footprint(p.Kind);
                float hw = (fp.HalfWidth * p.Scale) + PropMargin + halfDiagonal;
                float hd = (fp.HalfDepth * p.Scale) + PropMargin + halfDiagonal;
                float reach = MathF.Sqrt((hw * hw) + (hd * hd));
                float yaw = MathHelper.DegreesToRadians(p.YawDegrees);
                float sin = MathF.Sin(yaw);
                float cos = MathF.Cos(yaw);
                int minX = Math.Max(0, (int)MathF.Floor(p.Position.X - reach));
                int maxX = Math.Min(_width - 1, (int)MathF.Floor(p.Position.X + reach));
                int minZ = Math.Max(0, (int)MathF.Floor(p.Position.Z - reach));
                int maxZ = Math.Min(_depth - 1, (int)MathF.Floor(p.Position.Z + reach));
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        float px = x + 0.5f - p.Position.X;
                        float pz = z + 0.5f - p.Position.Z;
                        float localX = (px * cos) - (pz * sin);
                        float localZ = (px * sin) + (pz * cos);
                        if (MathF.Abs(localX) <= hw && MathF.Abs(localZ) <= hd)
                        {
                            _occupied[Index(x, z)] = true;
                        }
                    }
                }
            }
        }

        private void PlaceBig(GroundLayer layer, int bucket, uint percent, float margin, uint salt)
        {
            for (int bz = 0; bz * bucket < _depth; bz++)
            {
                for (int bx = 0; bx * bucket < _width; bx++)
                {
                    uint h = Hash(bx, bz, salt);
                    if (h % 100u >= percent)
                    {
                        continue;
                    }

                    float span = bucket - (2f * margin);
                    float x = (bx * bucket) + margin + (Frac(h, 4) * span);
                    float z = (bz * bucket) + margin + (Frac(h, 16) * span);
                    int cx = (int)x;
                    int cz = (int)z;
                    if (!Usable(cx, cz, out StaticGround.Column column) || NearEdge(cx, cz) || _occupied[Index(cx, cz)] ||
                        column.Material is GeometryMaterialId.GrassPath or GeometryMaterialId.Snow || OnRoad(x, z))
                    {
                        continue;
                    }

                    if (layer == GroundLayer.Landmark &&
                        ((x < PropLayout.AuthoredZoneX && z < PropLayout.AuthoredZoneZ) || DistanceSquared(x, z, SpawnX, SpawnZ) < SpawnClearance * SpawnClearance))
                    {
                        continue;
                    }

                    uint hp = Hash(bx, bz, salt ^ 0x9E37u);
                    int a = Pick(_suit[((int)layer * MaterialCount) + (int)column.Material], (hp & 0xFFFFu) / 65536f, false, 1f, false);
                    if (a < 0)
                    {
                        continue;
                    }

                    GroundAsset asset = _assets[a];
                    float scale = Lerp(asset.ScaleMin, asset.ScaleMax, (hp >> 16) / 65536f);
                    float yaw = Hash(bx, bz, salt ^ 0x7A3u) % 360u;
                    float fp = asset.Footprint * scale;
                    if (!PropLayout.TryResolveBase(_ground, x, z, yaw, new PropFootprint(fp, fp, 0), out float baseY))
                    {
                        continue;
                    }

                    float radius = asset.Radius * scale;
                    if (!Clear(x, z, radius))
                    {
                        continue;
                    }

                    _solids.Add(new Solid(x, z, radius));
                    _output.Add(new GroundPlacement(a, new Vector3(x, baseY, z), yaw, scale));
                }
            }
        }

        private bool Clear(float x, float z, float radius)
        {
            for (int i = 0; i < _solids.Count; i++)
            {
                float reach = radius + _solids[i].Radius;
                if (DistanceSquared(x, z, _solids[i].X, _solids[i].Z) < reach * reach)
                {
                    return false;
                }
            }

            for (int i = 0; i < _props.Count; i++)
            {
                PropPlacement p = _props[i];
                float reach = radius + (PropCatalog.Bounds(p.Kind).HorizontalRadius * p.Scale);
                if (DistanceSquared(x, z, p.Position.X, p.Position.Z) < reach * reach)
                {
                    return false;
                }
            }

            return true;
        }

        // The cells under an accent or a landmark (cell centres within its radius, and the cell it stands in): cover and
        // decals keep out, so grass does not poke through a log or a boulder.
        private void MarkBigFootprints()
        {
            for (int i = 0; i < _output.Count; i++)
            {
                GroundPlacement placed = _output[i];
                GroundAsset asset = _assets[placed.Asset];
                if (asset.Layer < GroundLayer.Accent)
                {
                    continue;
                }

                Vector3 p = placed.Position;
                float radius = asset.Radius * placed.Scale;
                int reach = (int)MathF.Ceiling(radius);
                int hx = (int)MathF.Floor(p.X);
                int hz = (int)MathF.Floor(p.Z);
                for (int z = Math.Max(0, hz - reach); z <= Math.Min(_depth - 1, hz + reach); z++)
                {
                    for (int x = Math.Max(0, hx - reach); x <= Math.Min(_width - 1, hx + reach); x++)
                    {
                        if ((x == hx && z == hz) || DistanceSquared(x + 0.5f, z + 0.5f, p.X, p.Z) <= radius * radius)
                        {
                            _occupied[Index(x, z)] = true;
                        }
                    }
                }
            }
        }

        // Cells within reach of a tree or a stump: the mushrooms gather there.
        private void MarkHosts()
        {
            int reach = (int)MathF.Ceiling(MushroomReach);
            for (int i = 0; i < _output.Count; i++)
            {
                GroundAssetClass c = _assets[_output[i].Asset].Class;
                if (c is not (GroundAssetClass.Tree or GroundAssetClass.Stump))
                {
                    continue;
                }

                Vector3 p = _output[i].Position;
                for (int z = Math.Max(0, (int)p.Z - reach); z <= Math.Min(_depth - 1, (int)p.Z + reach); z++)
                {
                    for (int x = Math.Max(0, (int)p.X - reach); x <= Math.Min(_width - 1, (int)p.X + reach); x++)
                    {
                        if (DistanceSquared(x + 0.5f, z + 0.5f, p.X, p.Z) <= MushroomReach * MushroomReach)
                        {
                            _nearHost[Index(x, z)] = true;
                        }
                    }
                }
            }
        }

        private void PlaceCover()
        {
            int layerBase = (int)GroundLayer.Cover * MaterialCount;
            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (!Usable(x, z, out StaticGround.Column column) || NearEdge(x, z) || _occupied[Index(x, z)] ||
                        column.Material is GeometryMaterialId.GrassPath or GeometryMaterialId.Snow)
                    {
                        continue;
                    }

                    int[] candidates = _suit[layerBase + (int)column.Material];
                    if (candidates.Length == 0)
                    {
                        continue;
                    }

                    float chance = (column.Material == GeometryMaterialId.Grass ? 0.30f : 0.12f) * Clump(x, z, ClumpSalt);
                    for (uint k = 0; k < 2; k++)
                    {
                        uint h = Hash(x, z, CoverSalt + k);
                        if ((h % 1000u) / 1000f >= chance)
                        {
                            continue;
                        }

                        uint hPos = Hash(x, z, CoverSalt + 16u + k);
                        uint hPick = Hash(x, z, CoverSalt + 32u + k);
                        int a = Pick(candidates, (hPick & 0xFFFFu) / 65536f, _nearHost[Index(x, z)], 1f, false);
                        if (a < 0)
                        {
                            continue;
                        }

                        GroundAsset asset = _assets[a];
                        float px = x + 0.1f + (Frac(hPos, 0) * 0.8f);
                        float pz = z + 0.1f + (Frac(hPos, 12) * 0.8f);
                        float scale = Lerp(asset.ScaleMin, asset.ScaleMax, (hPick >> 16) / 65536f);
                        float yaw = Hash(x, z, CoverSalt + 48u + k) % 360u;
                        _output.Add(new GroundPlacement(a, new Vector3(px, column.TopY + 1f, pz), yaw, scale));
                    }
                }
            }
        }

        private void PlaceDecals()
        {
            int layerBase = (int)GroundLayer.Decal * MaterialCount;
            float widest = 0f;
            for (int i = 0; i < _assets.Count; i++)
            {
                if (_assets[i].Layer == GroundLayer.Decal)
                {
                    widest = MathF.Max(widest, _assets[i].Radius * _assets[i].ScaleMax);
                }
            }

            int search = (int)MathF.Ceiling(widest * 2f) + 1;
            int[] spotAt = new int[_width * _depth];
            List<Solid> spots = [];
            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (!Usable(x, z, out StaticGround.Column column) || NearEdge(x, z) || _occupied[Index(x, z)])
                    {
                        continue;
                    }

                    int[] candidates = _suit[layerBase + (int)column.Material];
                    if (candidates.Length == 0)
                    {
                        continue;
                    }

                    uint h = Hash(x, z, DecalSalt);
                    if ((h % 10000u) / 10000f >= 0.07f * Clump(x, z, ClumpSalt + 1u))
                    {
                        continue;
                    }

                    float pathBoost = column.Material == GeometryMaterialId.GrassPath ? 6f : NextToPath(x, z) ? 3f : 1f;
                    uint hPick = Hash(x, z, DecalSalt + 1u);
                    int a = Pick(candidates, (hPick & 0xFFFFu) / 65536f, false, pathBoost, column.Material == GeometryMaterialId.Snow);
                    if (a < 0)
                    {
                        continue;
                    }

                    GroundAsset asset = _assets[a];
                    uint hPos = Hash(x, z, DecalSalt + 2u);
                    float px = x + 0.2f + (Frac(hPos, 0) * 0.6f);
                    float pz = z + 0.2f + (Frac(hPos, 12) * 0.6f);
                    float scale = Lerp(asset.ScaleMin, asset.ScaleMax, (hPick >> 16) / 65536f);
                    float yaw = Hash(x, z, DecalSalt + 3u) % 360u;
                    float radius = asset.Radius * scale;
                    // Half-extents reach past the disc by a cell's half diagonal, so every cell it touches is level.
                    float level = radius + 0.71f;
                    if (!PropLayout.TryResolveBase(_ground, px, pz, yaw, new PropFootprint(level, level, 0), out float baseY) ||
                        Overlaps(spotAt, spots, search, px, pz, radius))
                    {
                        continue;
                    }

                    spots.Add(new Solid(px, pz, radius));
                    spotAt[Index(x, z)] = spots.Count;
                    float lift = (Hash(x, z, DecalSalt + 4u) & 1u) == 0u ? DecalLiftA : DecalLiftB;
                    _output.Add(new GroundPlacement(a, new Vector3(px, baseY + lift, pz), yaw, scale));
                }
            }
        }

        private bool Overlaps(int[] spotAt, List<Solid> spots, int search, float x, float z, float radius)
        {
            int cx = (int)x;
            int cz = (int)z;
            for (int iz = Math.Max(0, cz - search); iz <= Math.Min(_depth - 1, cz + search); iz++)
            {
                for (int ix = Math.Max(0, cx - search); ix <= Math.Min(_width - 1, cx + search); ix++)
                {
                    int s = spotAt[Index(ix, iz)];
                    if (s == 0)
                    {
                        continue;
                    }

                    Solid other = spots[s - 1];
                    float reach = radius + other.Radius;
                    if (DistanceSquared(x, z, other.X, other.Z) < reach * reach)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool NextToPath(int x, int z) =>
            IsPath(x + 1, z) || IsPath(x - 1, z) || IsPath(x, z + 1) || IsPath(x, z - 1);

        private bool IsPath(int x, int z) => _ground.TryGet(x, z, out StaticGround.Column c) && c.Material == GeometryMaterialId.GrassPath;

        // The roads: the spawn lanes and the central cross, as PropLayout keeps them clear.
        private bool OnRoad(float x, float z) =>
            MathF.Abs(x - SpawnLane) < PropLayout.RoadClearance || MathF.Abs(z - SpawnLane) < PropLayout.RoadClearance ||
            MathF.Abs(x - (_width / 2)) < PropLayout.RoadClearance || MathF.Abs(z - (_depth / 2)) < PropLayout.RoadClearance;

        // Weighted pick among candidate asset indices; u in [0, 1).
        private int Pick(int[] candidates, float u, bool nearHost, float pathBoost, bool scorchOnly)
        {
            float total = 0f;
            for (int i = 0; i < candidates.Length; i++)
            {
                total += WeightOf(_assets[candidates[i]], nearHost, pathBoost, scorchOnly);
            }

            if (total <= 0f)
            {
                return -1;
            }

            float target = u * total;
            float sum = 0f;
            int last = -1;
            for (int i = 0; i < candidates.Length; i++)
            {
                float w = WeightOf(_assets[candidates[i]], nearHost, pathBoost, scorchOnly);
                if (w <= 0f)
                {
                    continue;
                }

                sum += w;
                last = candidates[i];
                if (target < sum)
                {
                    return last;
                }
            }

            return last;
        }

        private static float WeightOf(GroundAsset asset, bool nearHost, float pathBoost, bool scorchOnly)
        {
            if (scorchOnly && asset.Class != GroundAssetClass.DecalScorch)
            {
                return 0f;
            }

            float w = asset.Weight;
            if (asset.Class == GroundAssetClass.Mushroom && nearHost)
            {
                w *= MushroomBoost;
            }

            if (asset.Class == GroundAssetClass.DecalPath)
            {
                w *= pathBoost;
            }

            return w;
        }
    }

    // Value noise on a 12-cell lattice, mapped to a density factor with mean about 1.
    private static float Clump(int x, int z, uint salt)
    {
        float gx = x / (float)ClumpCells;
        float gz = z / (float)ClumpCells;
        int ix = (int)MathF.Floor(gx);
        int iz = (int)MathF.Floor(gz);
        float fx = gx - ix;
        float fz = gz - iz;
        fx = fx * fx * (3f - (2f * fx));
        fz = fz * fz * (3f - (2f * fz));
        float v = Lerp(Lerp(Lattice(ix, iz, salt), Lattice(ix + 1, iz, salt), fx), Lerp(Lattice(ix, iz + 1, salt), Lattice(ix + 1, iz + 1, salt), fx), fz);
        return 0.3f + (1.4f * v);
    }

    private static float Lattice(int x, int z, uint salt) => (Hash(x, z, salt) % 1000u) / 1000f;

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    // 12 bits of the hash starting at shift, as [0, 1).
    private static float Frac(uint h, int shift) => ((h >> shift) & 0xFFFu) / 4096f;

    private static float DistanceSquared(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return (dx * dx) + (dz * dz);
    }

    // The fixed integer hash PropLayout uses, with this layout's own salts.
    private static uint Hash(int a, int b, uint c)
    {
        uint h = ((uint)a * 0x9E3779B1u) ^ ((uint)b * 0x85EBCA77u) ^ (c * 0xC2B2AE3Du);
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }
}
