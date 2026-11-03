using Dream.Rendering.Lighting;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.Props;

// Where the props stand. A pure function of the replicated static content (StaticGround) and the authored
// brazier placements: no RNG, no clock, no local state, so every client derives the same list. It is client
// scenery - nothing here is simulated, persisted or checksummed, and nothing collides.
//
// Three sources:
//   1. AuthoredSites - the hamlet at the spawn, a hill graveyard on the island, two wolf-ground clearings, a
//      second hamlet and a ruined watch-post. Each is a hand-placed (kind, x, z, heading) on the default world's
//      terrain. A site is placed only where the ground under its footprint is solid, unobstructed and level
//      (buildings may span one step), so a world with other terrain drops it instead of burying it. The tests
//      assert that every site lands on the default terrain.
//   2. Braziers - one per PlacedLightIndex placement, standing on the ground the flame floats above. Those lights
//      cast a pool with no visible source today; this gives each one a bowl on a tripod.
//   3. Scatter - dead oaks, brambles, slate heaps, bone piles and lone graves hashed over the open ground by
//      integer bucket, away from the roads, the map edge and the authored areas.
public static class PropLayout
{
    public readonly record struct Site(PropKind Kind, float X, float Z, float YawDegrees);

    // Hand-placed scenery on the default terrain (WorldGenerator): x, z in world cells, heading 0 = front to +Z.
    // The camera looks from +X +Z, so a front toward +Z (0) or +X (90) faces the player.
    public static readonly Site[] AuthoredSites =
    [
        // The hamlet at the spawn (the player starts at 5, 5, the Quartermaster stands at 9, 7). A row of houses
        // faces the road on its west side: a cottage, the inn with the well and its barrels before it, a hovel, a
        // second cottage. The camera looks from +X +Z, so fronts toward +X (90) face the player.
        new(PropKind.Cottage, 2.5f, 8.0f, 90f),
        new(PropKind.Inn, 2.7f, 13.6f, 90f),
        new(PropKind.Hovel, 2.4f, 19.2f, 90f),
        new(PropKind.Cottage, 2.5f, 23.4f, 90f),
        new(PropKind.Well, 5.45f, 12.0f, 0f),
        new(PropKind.Cart, 5.9f, 18.4f, 70f),
        new(PropKind.RustBarrel, 4.7f, 9.9f, 0f),
        new(PropKind.RustBarrel, 4.95f, 16.0f, 0f),

        // The crossroads: a signpost, goods by the Quartermaster, a fence and a scarecrow on the north edge.
        new(PropKind.Signpost, 7.9f, 3.8f, 0f),
        new(PropKind.Crates, 7.6f, 9.4f, 25f),
        new(PropKind.Sacks, 6.9f, 10.2f, 40f),
        new(PropKind.RuneFlask, 7.0f, 8.9f, 0f),
        new(PropKind.Fence, 4.0f, 2.7f, 0f),
        new(PropKind.Fence, 5.6f, 2.7f, 0f),
        new(PropKind.Fence, 7.2f, 2.7f, 0f),
        new(PropKind.Scarecrow, 8.5f, 2.8f, 20f),
        new(PropKind.Woodpile, 1.4f, 4.6f, 90f),

        // The far side of the street, further down.
        new(PropKind.Cottage, 9.8f, 17.6f, 270f),
        new(PropKind.Woodpile, 8.0f, 21.8f, 0f),
        new(PropKind.Crates, 6.4f, 20.2f, -20f),
        new(PropKind.FenceBroken, 4.0f, 21.4f, 0f),

        // The island north-east of the spawn: a stone circle on the rise, a dead oak, and graves along its rim.
        new(PropKind.StandingStones, 13.5f, 9.0f, 0f),
        new(PropKind.DeadOak, 12.0f, 4.4f, 0f),
        new(PropKind.Bramble, 14.2f, 4.6f, 0f),
        new(PropKind.SlateRocks, 10.2f, 4.6f, 0f),
        new(PropKind.Tombstone, 16.9f, 5.0f, -30f),
        new(PropKind.Tombstone, 16.9f, 7.4f, -20f),
        new(PropKind.Tombstone, 17.0f, 9.8f, -35f),
        new(PropKind.Tombstone, 16.8f, 11.3f, -25f),
        new(PropKind.Tombstone, 9.2f, 13.2f, 20f),
        new(PropKind.Tombstone, 10.6f, 13.4f, 10f),
        new(PropKind.Tombstone, 12.0f, 13.2f, 25f),
        new(PropKind.BonePile, 9.4f, 11.2f, 0f),

        // Wolf ground by the north-east and south-west camps (content/spawns.json keeps those camps at 42.5, 8.5
        // and 8.5, 40.5; the tests check these stay within reach of them).
        new(PropKind.BonePile, 41.0f, 9.6f, 0f),
        new(PropKind.Bramble, 44.6f, 7.0f, 0f),
        new(PropKind.SlateRocks, 43.6f, 10.8f, 0f),
        new(PropKind.DeadOak, 39.4f, 6.4f, 0f),
        new(PropKind.BonePile, 9.8f, 41.6f, 0f),
        new(PropKind.Bramble, 6.0f, 39.0f, 0f),
        new(PropKind.SlateRocks, 11.2f, 38.8f, 0f),
        new(PropKind.Tombstone, 7.2f, 43.4f, 0f),

        // A second hamlet around the brazier anchor at 24, 40: a hunter's hovel, a woodpile, a cart, goods.
        new(PropKind.Hovel, 21.5f, 41.0f, 90f),
        new(PropKind.Woodpile, 21.6f, 43.6f, 90f),
        new(PropKind.Cart, 27.5f, 43.0f, 70f),
        new(PropKind.Sacks, 27.8f, 38.2f, 0f),
        new(PropKind.Crates, 23.0f, 37.0f, 10f),
        new(PropKind.Fence, 17.0f, 38.0f, 0f),
        new(PropKind.Scarecrow, 17.0f, 41.2f, 0f),

        // A ruined watch-post out on the plain.
        new(PropKind.RuinedWall, 38.0f, 27.0f, 0f),
        new(PropKind.StandingStones, 44.0f, 23.0f, 0f),
    ];

    // Scatter: one candidate per BucketSize x BucketSize bucket, with this probability (percent).
    public const int BucketSize = 9;
    public const int ScatterPercent = 40;

    // Open ground that scatter leaves to the authored areas (the village, the island, the sculptures).
    public const float AuthoredZoneX = 28f;
    public const float AuthoredZoneZ = 32f;

    // Scatter keeps this far from the roads (the spawn lanes and the central cross), the map edge, and any authored prop.
    public const float RoadClearance = 2.5f;
    public const float EdgeClearance = 4f;
    public const float SiteClearance = 3.5f;

    // The spawn lanes run through (5, 5); the central cross through the middle of the map (WorldGenerator).
    private const float SpawnLane = 5.5f;

    public static void Build(StaticGround ground, IReadOnlyList<PlacedLight> braziers, List<PropPlacement> output)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(braziers);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();

        for (int i = 0; i < AuthoredSites.Length; i++)
        {
            Site site = AuthoredSites[i];
            if (TryResolveBase(ground, site.X, site.Z, site.YawDegrees, PropCatalog.Footprint(site.Kind), out float baseY))
            {
                output.Add(new PropPlacement(site.Kind, new Vector3(site.X, baseY, site.Z), site.YawDegrees));
            }
        }

        for (int i = 0; i < braziers.Count; i++)
        {
            PlacedLight light = braziers[i];
            float yaw = (Hash(i, (int)light.Position.X, (int)light.Position.Z) % 360u);
            // The flame floats FlameHeight above the top face of the column, so the bowl's base is that far below it.
            output.Add(new PropPlacement(PropKind.Brazier, new Vector3(light.Position.X, light.Position.Y - PlacedLightIndex.FlameHeight, light.Position.Z), yaw));
        }

        ScatterWild(ground, output);

        output.Sort(ByKindThenPosition);
    }

    private static readonly Comparison<PropPlacement> ByKindThenPosition = static (a, b) =>
    {
        int k = a.Kind.CompareTo(b.Kind);
        if (k != 0)
        {
            return k;
        }

        int x = a.Position.X.CompareTo(b.Position.X);
        return x != 0 ? x : a.Position.Z.CompareTo(b.Position.Z);
    };

    private static void ScatterWild(StaticGround ground, List<PropPlacement> output)
    {
        int width = ground.MaxX + 1;
        int depth = ground.MaxZ + 1;
        int authored = output.Count;
        for (int bz = 0; bz * BucketSize < depth; bz++)
        {
            for (int bx = 0; bx * BucketSize < width; bx++)
            {
                uint h = Hash(bx, bz, 0x51ED);
                if (h % 100u >= ScatterPercent)
                {
                    continue;
                }

                float x = (bx * BucketSize) + 1.5f + (((h >> 8) % 1000u) / 1000f * (BucketSize - 3f));
                float z = (bz * BucketSize) + 1.5f + (((h >> 18) % 1000u) / 1000f * (BucketSize - 3f));
                if (x < AuthoredZoneX && z < AuthoredZoneZ)
                {
                    continue;
                }

                if (x < EdgeClearance || z < EdgeClearance || x > width - EdgeClearance || z > depth - EdgeClearance ||
                    MathF.Abs(x - SpawnLane) < RoadClearance || MathF.Abs(z - SpawnLane) < RoadClearance ||
                    MathF.Abs(x - (width / 2)) < RoadClearance || MathF.Abs(z - (depth / 2)) < RoadClearance)
                {
                    continue;
                }

                if (NearAnySite(output, authored, x, z))
                {
                    continue;
                }

                uint pick = Hash(bx, bz, 0xB10C) % 100u;
                float yaw = Hash(bx, bz, 0x7A3) % 360u;
                if (!ground.TryGet((int)x, (int)z, out StaticGround.Column column) || column.Blocked)
                {
                    continue;
                }

                PropKind kind = pick switch
                {
                    < 24 => PropKind.DeadOak,
                    < 58 => PropKind.Bramble,
                    < 82 => PropKind.SlateRocks,
                    < 93 => PropKind.BonePile,
                    _ => PropKind.Tombstone,
                };
                if (!SuitsGround(kind, column.Material) ||
                    !TryResolveBase(ground, x, z, yaw, PropCatalog.Footprint(kind), out float baseY))
                {
                    continue;
                }

                output.Add(new PropPlacement(kind, new Vector3(x, baseY, z), yaw));
            }
        }
    }

    // Trees and thorn grow on soil, bones lie on bare ground; slate and graves sit on anything but a road.
    private static bool SuitsGround(PropKind kind, GeometryMaterialId material) => kind switch
    {
        PropKind.DeadOak or PropKind.Bramble => material is GeometryMaterialId.Grass or GeometryMaterialId.Dirt or GeometryMaterialId.Mud,
        PropKind.BonePile => material is GeometryMaterialId.Dirt or GeometryMaterialId.Mud or GeometryMaterialId.Sand,
        _ => material != GeometryMaterialId.GrassPath && material != GeometryMaterialId.Lamp,
    };

    private static bool NearAnySite(List<PropPlacement> placed, int count, float x, float z)
    {
        for (int i = 0; i < count; i++)
        {
            float dx = placed[i].Position.X - x;
            float dz = placed[i].Position.Z - z;
            if ((dx * dx) + (dz * dz) < SiteClearance * SiteClearance)
            {
                return true;
            }
        }

        return false;
    }

    // The base height for a prop of this footprint at (x, z) turned yaw degrees: the top face of the highest column
    // under it, provided every column exists, is clear, and the tops differ by at most the footprint's MaxSpread.
    public static bool TryResolveBase(StaticGround ground, float x, float z, float yawDegrees, PropFootprint footprint, out float baseY)
    {
        baseY = 0f;
        float yaw = MathHelper.DegreesToRadians(yawDegrees);
        float sin = MathF.Sin(yaw);
        float cos = MathF.Cos(yaw);
        float radius = footprint.Radius;
        int minX = (int)MathF.Floor(x - radius);
        int maxX = (int)MathF.Floor(x + radius);
        int minZ = (int)MathF.Floor(z - radius);
        int maxZ = (int)MathF.Floor(z + radius);
        int low = int.MaxValue;
        int high = int.MinValue;
        for (int iz = minZ; iz <= maxZ; iz++)
        {
            for (int ix = minX; ix <= maxX; ix++)
            {
                float px = ix + 0.5f - x;
                float pz = iz + 0.5f - z;
                // The prop's own axes: forward (its +z) is (sin, cos) in world x, z.
                float localX = (px * cos) - (pz * sin);
                float localZ = (px * sin) + (pz * cos);
                if (MathF.Abs(localX) > footprint.HalfWidth || MathF.Abs(localZ) > footprint.HalfDepth)
                {
                    continue;
                }

                if (!ground.TryGet(ix, iz, out StaticGround.Column column) || column.Blocked)
                {
                    return false;
                }

                low = Math.Min(low, column.TopY);
                high = Math.Max(high, column.TopY);
            }
        }

        if (high == int.MinValue)
        {
            // A footprint narrower than a cell can miss every centre: use the cell it stands in.
            if (!ground.TryGet((int)MathF.Floor(x), (int)MathF.Floor(z), out StaticGround.Column own) || own.Blocked)
            {
                return false;
            }

            low = high = own.TopY;
        }

        if (high - low > footprint.MaxSpread)
        {
            return false;
        }

        baseY = high + 1f;
        return true;
    }

    // A fixed integer hash (the PlacedLightIndex mix), so placement never depends on a platform RNG.
    private static uint Hash(int a, int b, int c)
    {
        uint h = ((uint)a * 0x9E3779B1u) ^ ((uint)b * 0x85EBCA77u) ^ ((uint)c * 0xC2B2AE3Du);
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }
}
