using Dream.Rendering.Core;
using Dream.Rendering.Ground;
using Dream.Rendering.Lighting;
using Dream.Rendering.Props;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Tests.Rendering.Ground;

// Shared fixtures: a synthetic manifest with one asset per class (the real manifests come from the generators),
// the default terrain at both world sizes, and a fault sink that remembers what was reported.
internal static class GroundTestSets
{
    public const string Dir = "ground-test";

    private static readonly string[] AnyGround = ["grass", "dirt", "stone", "sand", "mud", "snow", "rock"];
    private static readonly string[] Soil = ["grass", "dirt", "mud"];

    public sealed class Faults : IRenderFaultSink
    {
        public List<string> Lines { get; } = [];

        public void Report(string source, Exception exception) => Lines.Add(exception.Message);
    }

    public static string Entry(string id, string cls, string layer, float radius, float footprint, float height, string[] ground, float weight = 1f, float scaleMin = 0.9f, float scaleMax = 1.1f) =>
        $$"""{ "id": "{{id}}", "file": "{{id.Replace('.', '/')}}.glb", "class": "{{cls}}", "layer": "{{layer}}", "radius": {{radius}}, "footprint": {{footprint}}, "height": {{height}}, "ground": [{{string.Join(", ", ground.Select(g => $"\"{g}\""))}}], "weight": {{weight}}, "scale": [{{scaleMin}}, {{scaleMax}}] }""";

    public static string Manifest(params string[] entries) =>
        $$"""{ "schema": "dream.ground/1", "family": "test", "generator": "test", "assets": [ {{string.Join(",\n", entries)}} ] }""";

    // One asset per class, sized like the plan's budgets.
    public static GroundAssetSet Synthetic()
    {
        string[] entries =
        [
            Entry("flora.tree", "tree", "landmark", 1.6f, 0.45f, 4.2f, Soil),
            Entry("flora.sapling", "sapling", "accent", 0.5f, 0.15f, 1.2f, Soil),
            Entry("flora.bush", "bush", "accent", 0.7f, 0.3f, 1f, Soil),
            Entry("stone.rock", "rock", "accent", 0.8f, 0.5f, 0.8f, AnyGround),
            Entry("stone.stump", "stump", "accent", 0.5f, 0.3f, 0.6f, Soil),
            Entry("stone.log", "log", "accent", 0.9f, 0.3f, 0.5f, Soil),
            Entry("flora.grass", "grass", "cover", 0.3f, 0.2f, 0.3f, ["grass", "dirt"]),
            Entry("flora.flower", "flower", "cover", 0.2f, 0.1f, 0.3f, ["grass"]),
            Entry("flora.mushroom", "mushroom", "cover", 0.2f, 0.1f, 0.2f, Soil),
            Entry("stone.root", "root", "cover", 0.5f, 0.3f, 0.2f, Soil),
            Entry("stone.pebbles", "pebbles", "cover", 0.3f, 0.2f, 0.1f, AnyGround),
            Entry("stone.twigs", "twigs", "cover", 0.3f, 0.2f, 0.1f, Soil),
            Entry("stone.leaves", "leaves", "cover", 0.3f, 0.2f, 0.1f, Soil),
            Entry("stone.bones", "bones", "cover", 0.3f, 0.2f, 0.1f, ["dirt", "mud", "sand"]),
            Entry("stone.item", "item", "cover", 0.3f, 0.2f, 0.2f, Soil),
            Entry("decals.decal_dirt", "decal.dirt", "decal", 0.9f, 0f, 0f, ["grass", "dirt"]),
            Entry("decals.decal_moss", "decal.moss", "decal", 0.8f, 0f, 0f, ["stone", "rock", "grass"]),
            Entry("decals.decal_puddle", "decal.puddle", "decal", 0.7f, 0f, 0f, ["mud", "dirt"]),
            Entry("decals.decal_crack", "decal.crack", "decal", 0.9f, 0f, 0f, ["stone", "rock"]),
            Entry("decals.decal_path", "decal.path", "decal", 1.2f, 0f, 0f, ["grass_path", "grass", "dirt"]),
            Entry("decals.decal_litter", "decal.litter", "decal", 0.8f, 0f, 0f, ["grass", "dirt"]),
            Entry("decals.decal_scorch", "decal.scorch", "decal", 0.8f, 0f, 0f, ["grass", "dirt", "stone", "sand", "mud", "snow", "rock"]),
            Entry("decals.decal_stain", "decal.stain", "decal", 0.7f, 0f, 0f, ["dirt", "stone", "sand"]),
        ];
        return GroundAssetSet.Parse([("synthetic.json", Manifest(entries))], Dir, new Faults());
    }

    public static (PropIndex Props, StaticGround Ground) World(int size)
    {
        Map map = new(width: size, depth: size);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(size / 2f, 2f, size / 2f), 400f);
        PlacedLightIndex lights = new();
        lights.Collect(export, new Vector3(8.5f, 2.5f, 8.5f), new LightCollector());
        PropIndex props = new();
        props.Update(export, lights);
        return (props, props.Ground);
    }

    public static List<GroundPlacement> Layout(StaticGround ground, IReadOnlyList<PropPlacement> props, GroundAssetSet assets)
    {
        List<GroundPlacement> output = [];
        GroundScatterLayout.Build(ground, props, assets, output);
        return output;
    }

    public static StaticGround Flat(int size, GeometryMaterialId material)
    {
        StaticGround ground = new();
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                ground.Add(x, 0, z, true, material);
            }
        }

        ground.Seal();
        return ground;
    }
}
