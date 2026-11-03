using System.Text.Json;
using Dream.Assets;
using Dream.Rendering.Lighting;
using Dream.Rendering.Models;
using Dream.Rendering.Props;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;

namespace Dream.Tests.Rendering.Props;

// The scenery layout: a pure function of the replicated static content (like PlacedLightIndex), client-visual
// only. These tests run it on the real default terrain at both world sizes the game uses.
public class PropLayoutTests
{
    private static readonly Vector3 Camera = new(8.5f, 2.5f, 8.5f);
    private static readonly Vector2 Spawn = new(5f, 5f);

    private static (PropIndex Props, PlacedLightIndex Lights, GeometryRenderExport Export) Build(int size)
    {
        Map map = new(width: size, depth: size);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(size / 2f, 2f, size / 2f), 400f);
        PlacedLightIndex lights = new();
        lights.Collect(export, Camera, new LightCollector());
        PropIndex props = new();
        props.Update(export, lights);
        return (props, lights, export);
    }

    public static IEnumerable<object[]> WorldSizes() => [[WorldDefaults.StartupFloorSize], [WorldDefaults.ServerNewWorldSize]];

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void EveryAuthoredSite_LandsOnTheDefaultTerrain(int size)
    {
        (PropIndex props, _, _) = Build(size);

        foreach (PropLayout.Site site in PropLayout.AuthoredSites)
        {
            Assert.True(
                props.Placements.Any(p => p.Kind == site.Kind && p.Position.X == site.X && p.Position.Z == site.Z && p.YawDegrees == site.YawDegrees),
                $"{site.Kind} at ({site.X}, {site.Z}) was dropped on a {size} world: the ground under it is not level, solid and clear.");
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void EveryPlacedProp_StandsOnTheTopFaceOfItsGround(int size)
    {
        (PropIndex props, _, GeometryRenderExport export) = Build(size);
        StaticGround ground = new();
        ground.Rebuild(export);

        foreach (PropPlacement p in props.Placements)
        {
            Assert.True(ground.TryGet((int)MathF.Floor(p.Position.X), (int)MathF.Floor(p.Position.Z), out StaticGround.Column column), $"{p.Kind} stands off the map");
            // The base is the top face of the highest column under the footprint: never below its own column's top,
            // and a building may stand one cell higher than the column it is centred on.
            Assert.InRange(p.Position.Y, column.TopY + 1f, column.TopY + 2f);
        }
    }

    [Fact]
    public void EveryLightAnchor_GetsABrazierBowlUnderItsFlame()
    {
        (PropIndex props, PlacedLightIndex lights, _) = Build(WorldDefaults.ServerNewWorldSize);

        Assert.NotEmpty(lights.Placements);
        foreach (PlacedLight light in lights.Placements)
        {
            Vector3 expected = new(light.Position.X, light.Position.Y - PlacedLightIndex.FlameHeight, light.Position.Z);
            Assert.Contains(props.Placements, p => p.Kind == PropKind.Brazier && p.Position == expected);
        }

        Assert.Equal(lights.Placements.Count, props.Placements.Count(p => p.Kind == PropKind.Brazier));
    }

    [Fact]
    public void Placements_AreAPureFunctionOfTheStaticContent_AndTheCubeFallbackAgrees()
    {
        (PropIndex a, _, GeometryRenderExport export) = Build(WorldDefaults.StartupFloorSize);
        (PropIndex b, _, _) = Build(WorldDefaults.StartupFloorSize);
        Assert.NotEmpty(a.Placements);
        Assert.Equal(a.Placements, b.Placements);

        GeometryRenderExport cubesOnly = new(export.DirtyCells, export.Cells, export.Blocks, export.CompatibilityCubes)
        {
            StaticContentVersion = export.StaticContentVersion,
        };
        PlacedLightIndex lights = new();
        lights.Collect(cubesOnly, Camera, new LightCollector());
        PropIndex fromCubes = new();
        fromCubes.Update(cubesOnly, lights);
        // The coarse cubes carry no subdivided cells, so the sculptures (blocked columns) are invisible to this path;
        // every placement the cell path made is still there, and the two lists differ only by props it could not see.
        Assert.All(a.Placements.Where(p => p.Kind != PropKind.Brazier && !fromCubes.Placements.Contains(p)), p =>
            Assert.True(
                p.Position.X > 13f && p.Position.X < 31f && p.Position.Z > 10f && p.Position.Z < 22f,
                $"{p.Kind} at {p.Position} differs between the cell path and the cube fallback outside the sculptures"));
    }

    [Fact]
    public void TheLayout_IsRescannedOnlyWhenTheStaticContentOrTheLightsChange()
    {
        (PropIndex props, PlacedLightIndex lights, GeometryRenderExport export) = Build(WorldDefaults.StartupFloorSize);
        int built = props.RebuildCount;

        props.Update(export, lights);
        props.Update(export, lights);
        Assert.Equal(built, props.RebuildCount);

        props.Update(export with { StaticContentVersion = export.StaticContentVersion + 1 }, lights);
        Assert.Equal(built + 1, props.RebuildCount);

        // Version 0 means unknown content: rescanned every call.
        GeometryRenderExport unknown = export with { StaticContentVersion = 0 };
        props.Update(unknown, lights);
        props.Update(unknown, lights);
        Assert.Equal(built + 3, props.RebuildCount);
    }

    [Fact]
    public void TheSpawnAndTheQuartermaster_AreNotBuiltOver()
    {
        (PropIndex props, _, _) = Build(WorldDefaults.ServerNewWorldSize);
        Vector2 quartermaster = new(9f, 7f);

        foreach (PropPlacement p in props.Placements)
        {
            float radius = PropCatalog.Footprint(p.Kind).Radius;
            Vector2 at = new(p.Position.X, p.Position.Z);
            Assert.True((at - Spawn).Length - radius >= 0.9f, $"{p.Kind} at {at} crowds the spawn");
            Assert.True((at - quartermaster).Length - radius >= 0.9f, $"{p.Kind} at {at} crowds the Quartermaster");
        }
    }

    [Fact]
    public void Scatter_StaysOffTheRoads_TheMapEdge_AndTheAuthoredZone()
    {
        (PropIndex props, _, _) = Build(WorldDefaults.ServerNewWorldSize);
        HashSet<PropPlacement> authored = [];
        foreach (PropLayout.Site s in PropLayout.AuthoredSites)
        {
            authored.Add(new PropPlacement(s.Kind, new Vector3(s.X, 0f, s.Z), s.YawDegrees));
        }

        List<PropPlacement> scatter = props.Placements
            .Where(p => p.Kind != PropKind.Brazier && !PropLayout.AuthoredSites.Any(s => s.Kind == p.Kind && s.X == p.Position.X && s.Z == p.Position.Z))
            .ToList();
        Assert.NotEmpty(scatter);
        foreach (PropPlacement p in scatter)
        {
            Assert.False(p.Position.X < PropLayout.AuthoredZoneX && p.Position.Z < PropLayout.AuthoredZoneZ, $"{p.Kind} scattered inside the authored zone");
            Assert.InRange(p.Position.X, PropLayout.EdgeClearance, WorldDefaults.ServerNewWorldSize - PropLayout.EdgeClearance);
            Assert.InRange(p.Position.Z, PropLayout.EdgeClearance, WorldDefaults.ServerNewWorldSize - PropLayout.EdgeClearance);
            Assert.True(MathF.Abs(p.Position.X - 50f) >= PropLayout.RoadClearance && MathF.Abs(p.Position.Z - 50f) >= PropLayout.RoadClearance, $"{p.Kind} on the central cross");
        }
    }

    [Fact]
    public void WolfGround_StaysBesideTheCampsTheContentKeeps()
    {
        // The two clearings are authored in client code; the camps live in content/spawns.json. If a camp moves,
        // this fails instead of leaving bones in an empty field.
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AssetPaths.Config(Path.Combine("content", "spawns.json"))));
        foreach ((string key, PropKind kind) in new[] { ("north-east-wolves", PropKind.BonePile), ("south-west-wolves", PropKind.BonePile) })
        {
            JsonElement camp = doc.RootElement.GetProperty("camps").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);
            Vector2 at = new(camp.GetProperty("x").GetSingle(), camp.GetProperty("z").GetSingle());
            Assert.Contains(
                PropLayout.AuthoredSites,
                s => s.Kind == kind && (new Vector2(s.X, s.Z) - at).Length <= 6f);
        }
    }

    [Fact]
    public void EveryKind_HasAShippedGlb_ThatTheEngineLoadsAsARigidModelWithAMaskedContactDisc()
    {
        Assert.Equal(PropCatalog.KindCount, Enum.GetValues<PropKind>().Length);
        foreach (PropKind kind in Enum.GetValues<PropKind>())
        {
            string path = PropCatalog.ModelPath(kind);
            Assert.True(File.Exists(path), $"{kind}: {path} is not shipped (run tools/ui-assets/gen_props.py)");

            // The real decode path (SharpGLTF validates every accessor: a non-unit normal throws here).
            AnimatedModelData data = AnimatedModelData.Load(path);
            Assert.Empty(data.Model.LogicalSkins);
            Assert.Empty(data.Model.LogicalAnimations);
            Assert.True(data.Model.LogicalMeshes.Count >= 1);
            Assert.Contains(data.Model.LogicalMaterials, m => m.Alpha == AlphaMode.MASK);
            Assert.Contains(data.Model.LogicalMaterials, m => m.FindChannel("BaseColor")?.Texture is not null);
        }
    }
}
