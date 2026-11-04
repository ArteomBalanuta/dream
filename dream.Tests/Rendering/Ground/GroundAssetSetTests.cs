using Dream.Rendering.Ground;
using Dream.Rendering.Models;
using Dream.Simulation.World.Geometry;
using SharpGLTF.Schema2;
using Xunit;

namespace Dream.Tests.Rendering.Ground;

// The manifest reader: the contract shared with the generators (schema dream.ground/1), lenient about entries it
// cannot use, never throwing.
public class GroundAssetSetTests
{
    [Fact]
    public void AnInlineManifest_Parses_IntoTypedAssets()
    {
        GroundTestSets.Faults faults = new();
        string json = GroundTestSets.Manifest(
            GroundTestSets.Entry("flora.tree_gallows_oak", "tree", "landmark", 1.6f, 0.45f, 4.2f, ["grass", "dirt", "mud"], 2f, 0.85f, 1.15f),
            GroundTestSets.Entry("decals.decal_path_01", "decal.path", "decal", 1.1f, 0f, 0f, ["grass_path", "grass"]));

        GroundAssetSet set = GroundAssetSet.Parse([("flora.json", json)], "root", faults);

        Assert.Empty(faults.Lines);
        Assert.Equal(2, set.Assets.Count);
        GroundAsset tree = set.Assets[0];
        Assert.Equal("flora.tree_gallows_oak", tree.Id);
        Assert.Equal(Path.Combine("root", "flora", "tree_gallows_oak.glb"), tree.ModelPath);
        Assert.Equal(GroundAssetClass.Tree, tree.Class);
        Assert.Equal(GroundLayer.Landmark, tree.Layer);
        Assert.Equal(1.6f, tree.Radius);
        Assert.Equal(0.45f, tree.Footprint);
        Assert.Equal(4.2f, tree.Height);
        Assert.Equal([GeometryMaterialId.Grass, GeometryMaterialId.Dirt, GeometryMaterialId.Mud], tree.Ground);
        Assert.Equal(2f, tree.Weight);
        Assert.Equal(0.85f, tree.ScaleMin);
        Assert.Equal(1.15f, tree.ScaleMax);
        GroundAsset path = set.Assets[1];
        Assert.Equal(GroundAssetClass.DecalPath, path.Class);
        Assert.Equal(GroundLayer.Decal, path.Layer);
        Assert.Contains(GeometryMaterialId.GrassPath, path.Ground);
    }

    [Fact]
    public void ModelPaths_ResolveUnderTheGivenDirectory()
    {
        string json = GroundTestSets.Manifest(
            "{ \"id\": \"flora.a\", \"file\": \"flora/a.glb\", \"class\": \"grass\", \"layer\": \"cover\", \"radius\": 0.2, \"footprint\": 0.1, \"height\": 0.2, \"ground\": [\"grass\"], \"weight\": 1, \"scale\": [1, 1] }");

        GroundAssetSet set = GroundAssetSet.Parse([("flora.json", json)], "root", new GroundTestSets.Faults());

        Assert.Equal(Path.Combine("root", "flora", "a.glb"), set.Assets.Single().ModelPath);
    }

    [Fact]
    public void UnusableEntries_AreSkippedAndReported_NotThrown()
    {
        GroundTestSets.Faults faults = new();
        string good = GroundTestSets.Entry("flora.good", "grass", "cover", 0.2f, 0.1f, 0.2f, ["grass"]);
        string json = GroundTestSets.Manifest(
            good,
            GroundTestSets.Entry("flora.badclass", "tumbleweed", "cover", 0.2f, 0.1f, 0.2f, ["grass"]),
            GroundTestSets.Entry("flora.badlayer", "grass", "sky", 0.2f, 0.1f, 0.2f, ["grass"]),
            GroundTestSets.Entry("flora.badmaterial", "grass", "cover", 0.2f, 0.1f, 0.2f, ["grass", "lava"]),
            GroundTestSets.Entry("flora.decalcover", "decal.dirt", "cover", 0.2f, 0.1f, 0.2f, ["grass"]),
            GroundTestSets.Entry("flora.grassdecal", "grass", "decal", 0.2f, 0.1f, 0.2f, ["grass"]),
            GroundTestSets.Entry("flora.noradius", "grass", "cover", 0f, 0.1f, 0.2f, ["grass"]),
            GroundTestSets.Entry("flora.noground", "grass", "cover", 0.2f, 0.1f, 0.2f, []),
            good);

        GroundAssetSet set = GroundAssetSet.Parse([("flora.json", json)], GroundTestSets.Dir, faults);

        Assert.Equal(["flora.good"], set.Assets.Select(a => a.Id));
        Assert.Equal(8, faults.Lines.Count);
        Assert.Contains(faults.Lines, l => l.Contains("tumbleweed"));
        Assert.Contains(faults.Lines, l => l.Contains("sky"));
        Assert.Contains(faults.Lines, l => l.Contains("lava"));
        Assert.Contains(faults.Lines, l => l.Contains("repeated id"));
    }

    [Fact]
    public void MalformedDocuments_AreReported_AndTheRestStillLoads()
    {
        GroundTestSets.Faults faults = new();
        string good = GroundTestSets.Manifest(GroundTestSets.Entry("stone.rock", "rock", "accent", 0.8f, 0.5f, 0.8f, ["stone"]));

        GroundAssetSet set = GroundAssetSet.Parse([("broken.json", "{ not json"), ("empty.json", "{}"), ("stone.json", good)], GroundTestSets.Dir, faults);

        Assert.Single(set.Assets);
        Assert.Equal(2, faults.Lines.Count);
    }

    [Fact]
    public void NoDocuments_OrAMissingDirectory_IsTheEmptySet()
    {
        Assert.Same(GroundAssetSet.Empty, GroundAssetSet.Parse([], GroundTestSets.Dir));
        Assert.Same(GroundAssetSet.Empty, GroundAssetSet.LoadDirectory(Path.Combine(Path.GetTempPath(), "dream-no-such-ground-dir-" + Guid.NewGuid().ToString("N"))));
        Assert.Empty(GroundAssetSet.Empty.Assets);
    }

    [Fact]
    public void LoadDirectory_ReadsEveryManifest_InNameOrder()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "ground-set-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "stone.json"), GroundTestSets.Manifest(GroundTestSets.Entry("stone.rock", "rock", "accent", 0.8f, 0.5f, 0.8f, ["stone"])));
            File.WriteAllText(Path.Combine(dir, "flora.json"), GroundTestSets.Manifest(GroundTestSets.Entry("flora.grass", "grass", "cover", 0.2f, 0.1f, 0.2f, ["grass"])));

            GroundAssetSet set = GroundAssetSet.LoadDirectory(dir);

            Assert.Equal(["flora.grass", "stone.rock"], set.Assets.Select(a => a.Id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Empty until the generators' output is merged; then every shipped entry must be a real, loadable, rigid model.
    [Fact]
    public void EveryShippedEntry_HasAGlb_ThatTheEngineLoadsAsARigidModel()
    {
        GroundAssetSet set = GroundAssetSet.LoadShipped();

        Assert.Equal(set.Assets.Count, set.Assets.Select(a => a.Id).Distinct().Count());
        foreach (GroundAsset asset in set.Assets)
        {
            Assert.True(File.Exists(asset.ModelPath), $"{asset.Id}: {asset.ModelPath} is not shipped");
            AnimatedModelData data = AnimatedModelData.Load(asset.ModelPath);
            Assert.Empty(data.Model.LogicalSkins);
            Assert.Empty(data.Model.LogicalAnimations);
            Assert.True(data.Model.LogicalMeshes.Count >= 1, asset.Id);
            Assert.Contains(data.Model.LogicalMaterials, m => m.FindChannel("BaseColor")?.Texture is not null);
            if (asset.Layer == GroundLayer.Decal)
            {
                Assert.Contains(data.Model.LogicalMaterials, m => m.Alpha == AlphaMode.MASK);
            }

            Assert.True(asset.Radius > 0f && asset.Weight > 0f && asset.ScaleMin <= asset.ScaleMax, asset.Id);
        }
    }
}
