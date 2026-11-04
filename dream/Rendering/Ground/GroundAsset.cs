using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.Ground;

// Where an asset sits in the scene: decals lie on the floor, cover is knee-high clutter, accents and landmarks are
// the bigger pieces (bushes, boulders, trees). The layer picks the program and the draw distance.
public enum GroundLayer { Decal, Cover, Accent, Landmark }

public enum GroundAssetClass
{
    Tree, Sapling, Bush, Grass, Flower, Mushroom, Root, Rock, Pebbles, Stump, Log, Twigs, Leaves, Bones, Item,
    DecalDirt, DecalMoss, DecalPuddle, DecalCrack, DecalPath, DecalLitter, DecalScorch, DecalStain,
}

// One entry of a dream/animations/ground/<family>.json manifest (schema dream.ground/1). Sizes are world units:
// Radius is the largest horizontal vertex distance from the origin, Footprint the half extent of the geometry
// near the ground, Height the largest y. Ground lists the materials the asset may stand on.
public sealed record GroundAsset(
    string Id,
    string ModelPath,
    GroundAssetClass Class,
    GroundLayer Layer,
    float Radius,
    float Footprint,
    float Height,
    IReadOnlyList<GeometryMaterialId> Ground,
    float Weight,
    float ScaleMin,
    float ScaleMax);
