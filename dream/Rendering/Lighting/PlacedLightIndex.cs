using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// The authored braziers: the only light the world seed provides (lamps are player-built, torches
// are held). Placement is a pure function of the replicated static content, so every client derives
// the same braziers and no local RNG is involved.
//
// Placement. The ground plane is cut into Spacing x Spacing buckets. Each bucket holds at most one
// brazier, in a column near the bucket centre that a fixed integer hash of the bucket index shifts
// by -Jitter..+Jitter cells on each axis. The flame sits FlameHeight above the topmost static solid
// cell of that column. A bucket whose column is empty or topped by a lamp gets none. Two braziers
// are therefore at least Spacing - 2 * Jitter = 14 units apart, and the number within
// CollectDistance of any camera is bounded (see CollectDistance), so the 16-slot LightList chosen
// by LightSelection.SelectNearest does not swap lights in and out as the camera moves. Cell order
// does not matter, so two exports with the same cells give the same list.
//
// Like LampLightIndex the scan runs once per StaticContentVersion change (an unknown version, 0, is
// rescanned every frame) into reused lists, and a steady-state Collect allocates nothing. Editing
// the terrain under a brazier moves its flame with the surface; that is replicated content too.
//
// Radius. BrazierRadius deliberately equals LampLightIndex.LampRadius: FxSimulation.TryClassify tags
// a light by radius, so a brazier inherits the lamp's fire halo and drifting embers, which is the
// right behaviour for a burning bowl. A different radius would give it no FX at all.
public sealed class PlacedLightIndex
{
    public const float BrazierRadius = LampLightIndex.LampRadius;

    // Linear RGB times intensity: a deep orange fire, hotter than the held torch (1.6) so a brazier
    // is the brightest thing on the ground.
    public static readonly Vector3 BrazierColor = new Vector3(1.0f, 0.72f, 0.46f) * 1.68f;

    public const int Spacing = 16;
    public const int Jitter = 1;

    // Flame centre above the top face of the column's highest cell: a bowl on a short stand.
    public const float FlameHeight = 1.0f;

    // Only braziers within this distance of the camera are added. Short of the render distance plus
    // the radius on purpose: ground this far out is mostly fogged at night, and at Spacing 16 a disc
    // of 30 holds about 11 braziers (13 at most for any camera), which together with the held and
    // lamp lights stays inside LightList.MaxLights.
    public const float CollectDistance = 30f;

    private const float CollectDistanceSquared = CollectDistance * CollectDistance;

    private static readonly Comparison<PlacedLight> ByPosition = static (a, b) =>
    {
        int x = a.Position.X.CompareTo(b.Position.X);
        return x != 0 ? x : a.Position.Z.CompareTo(b.Position.Z);
    };

    private readonly List<PlacedLight> _placements = [];
    private readonly Dictionary<long, int> _columnTops = [];
    private long _staticVersion;
    private bool _hasStaticScan;

    // Times the static content was rescanned (tests).
    public int StaticRescanCount { get; private set; }

    // Braziers added by the last Collect (within CollectDistance, before the collector's capacity).
    public int LastCollectedCount { get; private set; }

    // The placements of the current static content, sorted by X then Z. Shared and reused: callers
    // must not mutate. Valid after the first Collect.
    public IReadOnlyList<PlacedLight> Placements => _placements;

    // How much of a brazier's light shows at this sun intensity (FrameLighting.SunDirection.W, which the
    // sky model already fades at the horizon): 1 at night and in the dusk band (sun 0), eased to 0 by
    // SunFadeEnd, so the pools fade out through the morning and are gone at noon. A fire is a strong local
    // source at night and nearly invisible in daylight.
    public const float SunFadeEnd = 0.55f;

    public static float DaylightFactor(float sunIntensity)
    {
        float t = Math.Clamp(sunIntensity / SunFadeEnd, 0f, 1f);
        return 1f - t * t * (3f - 2f * t);
    }

    // sunIntensity: the frame's sun intensity (0 = night); scales every brazier and drops them once the
    // factor is negligible, so a daylight frame carries no halo or embers for them either.
    public void Collect(in GeometryRenderExport export, Vector3 cameraPosition, LightCollector lights, float sunIntensity = 0f)
    {
        ArgumentNullException.ThrowIfNull(lights);
        long version = export.StaticContentVersion;
        if (!_hasStaticScan || version == 0 || version != _staticVersion)
        {
            RescanStatic(in export);
            _staticVersion = version;
            _hasStaticScan = true;
        }

        int count = 0;
        float strength = DaylightFactor(sunIntensity);
        if (strength < 0.01f)
        {
            LastCollectedCount = 0;
            return;
        }

        for (int i = 0; i < _placements.Count; i++)
        {
            PlacedLight placed = _placements[i];
            if (Vector3.DistanceSquared(placed.Position, cameraPosition) <= CollectDistanceSquared)
            {
                lights.Add(new PointLight(placed.Position, placed.Radius, placed.Color * strength));
                count++;
            }
        }

        LastCollectedCount = count;
    }

    // The column that bucket (bx, bz) may hold its brazier in.
    public static (int X, int Z) AnchorColumn(int bx, int bz)
    {
        uint h = Hash(bx, bz);
        int jx = (int)(h % (2 * Jitter + 1)) - Jitter;
        int jz = (int)((h >> 16) % (2 * Jitter + 1)) - Jitter;
        return (bx * Spacing + Spacing / 2 + jx, bz * Spacing + Spacing / 2 + jz);
    }

    private static uint Hash(int bx, int bz)
    {
        uint h = ((uint)bx * 0x9E3779B1u) ^ ((uint)bz * 0x85EBCA77u);
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static long Pack(int bx, int bz) => ((long)bx << 32) | (uint)bz;

    // Keeps the encoded top of the bucket's anchor column: (y << 1) | isLamp, highest cell wins.
    private void Consider(int x, int y, int z, bool lamp)
    {
        int bx = FloorDiv(x, Spacing);
        int bz = FloorDiv(z, Spacing);
        (int ax, int az) = AnchorColumn(bx, bz);
        if (x != ax || z != az)
        {
            return;
        }

        long key = Pack(bx, bz);
        int encoded = (y << 1) | (lamp ? 1 : 0);
        if (!_columnTops.TryGetValue(key, out int best) || (encoded >> 1) > (best >> 1))
        {
            _columnTops[key] = encoded;
        }
    }

    private void RescanStatic(in GeometryRenderExport export)
    {
        StaticRescanCount++;
        _placements.Clear();
        _columnTops.Clear();

        IReadOnlyList<GeometryRenderCell>? staticCells = export.StaticCells;
        if (staticCells != null)
        {
            for (int i = 0; i < staticCells.Count; i++)
            {
                GeometryRenderCell cell = staticCells[i];
                if (cell.Cell.Definition.IsSolid)
                {
                    Consider(cell.Key.X, cell.Key.Y, cell.Key.Z, cell.Cell.Definition.MaterialId == GeometryMaterialId.Lamp);
                }
            }
        }
        else if (export.CompatibilityCubes != null)
        {
            IReadOnlyList<Cube> cubes = export.CompatibilityCubes;
            for (int i = 0; i < cubes.Count; i++)
            {
                Cube cube = cubes[i];
                if (!cube.IsEmpty && cube.IsSolid)
                {
                    Consider(
                        (int)MathF.Floor(cube.Position.X),
                        (int)MathF.Floor(cube.Position.Y),
                        (int)MathF.Floor(cube.Position.Z),
                        cube.MaterialId == GeometryMaterialId.Lamp);
                }
            }
        }

        foreach ((long key, int encoded) in _columnTops)
        {
            if ((encoded & 1) != 0)
            {
                continue;
            }

            int bx = (int)(key >> 32);
            int bz = (int)(uint)key;
            (int x, int z) = AnchorColumn(bx, bz);
            _placements.Add(new PlacedLight(
                new Vector3(x + 0.5f, (encoded >> 1) + 1f + FlameHeight, z + 0.5f),
                BrazierRadius,
                BrazierColor));
        }

        _placements.Sort(ByPosition);
    }
}
