using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

// Noise-driven material distribution: continuous fields thresholded into weights, instead of the
// classic policy's chained modulo tests on a 3x3 block hash.
//
// Three passes, in the order the ground reads them:
//   1. Macro layout. Two independently seeded fBm fields, elevation and moisture, decide basin,
//      field and high ground and wet or dry. Four octaves at about one feature per 40 cells.
//   2. Stone scatter. Worley noise gives discrete stone bodies (f1) and the fracture lines between
//      them (f2 - f1). It is multiplied by the slope-and-elevation mask, so stones cluster on
//      mounds and never float in a flat basin -- unmasked Worley reads as more artificial than the
//      modulo hash it replaces.
//   3. Choice. Every material gets a weight; the largest wins. A small per-cell dither is added
//      before the comparison so the line between two near-equal weights is ragged at cell scale
//      rather than a clean contour, which is what makes a voxel transition read as organic.
//
// The hand-placed landmarks (the rim ring, the spawn and central paths, the spawn island rim and
// its bridge) are resolved before the fields: they are gameplay-legible and the noise must not
// erode them.
//
// Deliberately seed-independent. A client rebuilds a generated world from its descriptor, and the
// simulation seed is server-only (WorldDescriptor), so a seed-dependent generator would give the
// client different ground from the server's. The sub-seeds below are fixed constants.
public sealed class NaturalTerrainMaterialPolicy : ITerrainMaterialPolicy
{
    public static NaturalTerrainMaterialPolicy Instance { get; } = new();

    // Fixed sub-seeds, one field per concern, so tuning one does not move the others. What
    // separates them is their high bytes, not the 0001..0004 tails: Fbm adds the octave index to
    // the seed it is given and Worley adds the golden ratio, so neighbouring values would collide
    // with each other's derived seeds. A fifth constant goes a long way from these four, not one
    // past the last tail.
    private const ulong ElevationSeed = 0x5EEDE1E7A7100001UL;
    private const ulong MoistureSeed = 0x5EED001570120002UL;
    private const ulong StoneSeed = 0x5EED57030E000003UL;
    private const ulong DitherSeed = 0x5EEDD17432000004UL;

    // Cells per feature, as a frequency. Tuned against the 56x56 startup world: at 1/8 the result
    // is noise again, and at 1/40 the field holds barely one period, so a startup world gets one
    // basin if it is lucky and no sand at all. Two periods across the world is the balance.
    private const float ElevationFrequency = 1f / 28f;
    private const float MoistureFrequency = 1f / 21f;
    private const float StoneFrequency = 1f / 14f;

    private const int ElevationOctaves = 4;
    private const int MoistureOctaves = 3;

    // Measured on the 56x56 startup world, the raw fBm fields run about 0.25..0.73 with their
    // middle half inside 0.47..0.59, so every ramp below would sit in the tail of a field that
    // never gets there. This gain spreads that middle half across most of 0..1 (see
    // TerrainNoise.Contrast) and is what makes a basin a basin.
    private const float FieldContrast = 2.8f;

    // Grass is the ground of last resort: it wins wherever no other weight makes its case.
    private const float GrassFloor = 0.14f;

    // Small on purpose: enough to fray a boundary by a cell or two, not enough to speckle the
    // interior of a patch. Raising it undoes the coherence the fields buy.
    private const float DitherAmplitude = 0.04f;

    // How hard stone argues for a cell once the mask lets it. Below 1 so a scattered stone body
    // has to sit on dry or high ground to beat the grass around it; above 1 on real slope, where
    // bare stone is what a cliff face is.
    private const float StoneGain = 0.78f;
    private const float SteepStoneGain = 1.15f;

    public GeometryMaterialId ResolveFloorMaterial(int x, int z, int width, int depth)
    {
        int rimDistance = TerrainLandmarks.RimDistance(x, z, width, depth);
        if (rimDistance <= 2)
        {
            return rimDistance == 0
                ? GeometryMaterialId.Stone
                : GeometryMaterialId.Rock;
        }

        if (TerrainLandmarks.IsSpawnPath(x, z, width, depth))
        {
            return GeometryMaterialId.Dirt;
        }

        if (TerrainLandmarks.IsCentralPath(x, z, width, depth))
        {
            return GeometryMaterialId.GrassPath;
        }

        return ResolveSurfaceMaterial(x, z, width, depth, raised: false);
    }

    public GeometryMaterialId ResolveRaisedMaterial(int x, int z, int width, int depth, int y, int raisedHeight)
    {
        // Buried cells are never seen; the classic policy fills them with stone and so does this.
        if (y < raisedHeight)
        {
            return GeometryMaterialId.Stone;
        }

        if (TerrainLandmarks.IsSpawnBridge(x, z))
        {
            return GeometryMaterialId.Dirt;
        }

        if (TerrainLandmarks.IsSpawnIsland(x, z) && TerrainLandmarks.SpawnIslandRimDistance(x, z) <= 1)
        {
            return GeometryMaterialId.Stone;
        }

        return ResolveSurfaceMaterial(x, z, width, depth, raised: true);
    }

    // The weighted choice. raised drops the standing-water materials: sand and mud belong in a
    // basin, not on the top of a mound, however dry or wet the moisture field says it is.
    private static GeometryMaterialId ResolveSurfaceMaterial(int x, int z, int width, int depth, bool raised)
    {
        float elevation = TerrainNoise.Contrast(
            TerrainNoise.FbmUnit(
                x * ElevationFrequency,
                z * ElevationFrequency,
                ElevationSeed,
                ElevationOctaves),
            FieldContrast);
        float moisture = TerrainNoise.Contrast(
            TerrainNoise.FbmUnit(
                x * MoistureFrequency,
                z * MoistureFrequency,
                MoistureSeed,
                MoistureOctaves),
            FieldContrast);
        float slope = ResolveSlope(x, z, width, depth);

        // Elevation decides the zone. land is deliberately wide -- it is ordinary ground, the bulk
        // of a world -- while basin and high are the two tails. A narrow band for the middle was
        // the first mistake here: the product of two narrow bands is near zero almost everywhere,
        // which left grass unable to win its own ground and filled the world with stone.
        float basin = raised ? 0f : 1f - TerrainNoise.Smoothstep(0.26f, 0.46f, elevation);
        float land = TerrainNoise.Smoothstep(0.10f, 0.30f, elevation) *
            (1f - TerrainNoise.Smoothstep(0.74f, 0.94f, elevation));
        float high = TerrainNoise.Smoothstep(0.70f, 0.92f, elevation);

        // Moisture splits the zone, monotonically: wetter is grass and drier is dirt on ordinary
        // ground, and the extremes are mud and sand once the ground is also low.
        float grassAffinity = TerrainNoise.Smoothstep(0.28f, 0.52f, moisture);
        float dryAffinity = 1f - TerrainNoise.Smoothstep(0.36f, 0.60f, moisture);
        float veryWet = TerrainNoise.Smoothstep(0.58f, 0.80f, moisture);
        float veryDry = 1f - TerrainNoise.Smoothstep(0.20f, 0.44f, moisture);

        // Pass 2. outcrop is the body of a stone. f2 - f1 is near zero exactly between two
        // neighbouring bodies, and that seam is not stone: it is the earth in the crack, so it
        // multiplies the mask down instead of selecting a second stone material. Cracks a cell
        // wide would only trade the modulo hash's speckle for a different speckle.
        TerrainNoise.Worley(x * StoneFrequency, z * StoneFrequency, StoneSeed, out float f1, out float f2);
        float outcrop = 1f - TerrainNoise.Smoothstep(0.20f, 0.50f, f1);
        float seam = TerrainNoise.Smoothstep(0.02f, 0.09f, f2 - f1);
        float steep = TerrainNoise.Smoothstep(0.35f, 1.1f, slope);
        float rocky = MathF.Max(steep, raised ? MathF.Max(high, 0.5f) : high);
        // Real slope outranks grass on its own; a scattered body has to argue against it.
        float stoneMask = MathF.Max(steep * SteepStoneGain, outcrop * rocky * StoneGain) * seam;

        float bestWeight = GrassFloor + (0.90f * land * grassAffinity) + Dither(x, z, 0);
        GeometryMaterialId best = GeometryMaterialId.Grass;
        TryTake(ref best, ref bestWeight, GeometryMaterialId.Dirt, (0.80f * land * dryAffinity) + Dither(x, z, 1));
        TryTake(ref best, ref bestWeight, GeometryMaterialId.Sand, (1.00f * basin * veryDry) + Dither(x, z, 2));
        TryTake(ref best, ref bestWeight, GeometryMaterialId.Mud, (1.00f * basin * veryWet) + Dither(x, z, 3));
        // Rock is the bare, weathered face: the same stone bodies, where the ground is high.
        TryTake(
            ref best,
            ref bestWeight,
            GeometryMaterialId.Stone,
            (stoneMask * (1f - high)) + Dither(x, z, 4));
        TryTake(ref best, ref bestWeight, GeometryMaterialId.Rock, (stoneMask * high) + Dither(x, z, 5));
        return best;
    }

    // The steepness of the real terrain, not of a second invented height field: the largest step to
    // a four-neighbour of this cell's landmark height, in cells. Out-of-bounds neighbours are
    // clamped so the world edge does not read as a cliff (the rim ring owns those cells anyway).
    private static float ResolveSlope(int x, int z, int width, int depth)
    {
        int here = WorldGenerator.ResolveRaisedLandmarkHeight(x, z, width, depth);
        int step = 0;
        step = Math.Max(step, NeighbourStep(here, x - 1, z, width, depth));
        step = Math.Max(step, NeighbourStep(here, x + 1, z, width, depth));
        step = Math.Max(step, NeighbourStep(here, x, z - 1, width, depth));
        step = Math.Max(step, NeighbourStep(here, x, z + 1, width, depth));
        return step;
    }

    private static int NeighbourStep(int here, int x, int z, int width, int depth)
    {
        int clampedX = Math.Clamp(x, 0, width - 1);
        int clampedZ = Math.Clamp(z, 0, depth - 1);
        return Math.Abs(WorldGenerator.ResolveRaisedLandmarkHeight(clampedX, clampedZ, width, depth) - here);
    }

    // A fresh hash per material, so the dither does not correlate between competing weights.
    private static float Dither(int x, int z, int channel)
    {
        return (TerrainNoise.Hash01(x, z, DitherSeed + (ulong)channel) - 0.5f) * DitherAmplitude;
    }

    private static void TryTake(
        ref GeometryMaterialId best,
        ref float bestWeight,
        GeometryMaterialId candidate,
        float weight)
    {
        if (weight > bestWeight)
        {
            bestWeight = weight;
            best = candidate;
        }
    }
}
