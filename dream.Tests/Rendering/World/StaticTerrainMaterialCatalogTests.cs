using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticTerrainMaterialCatalogTests
{
    [Theory]
    [InlineData(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Top, "library/forge/grass_top/grass_top.albedo.png")]
    [InlineData(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Side, "library/forge/grass_side/grass_side.albedo.png")]
    [InlineData(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Bottom, "library/forge/grass_bottom/grass_bottom.albedo.png")]
    [InlineData(GeometryMaterialId.Dirt, StaticTerrainMaterialRole.Single, "library/forge/dirt/dirt.albedo.png")]
    [InlineData(GeometryMaterialId.Stone, StaticTerrainMaterialRole.Single, "library/forge/stone/stone.albedo.png")]
    [InlineData(GeometryMaterialId.Sand, StaticTerrainMaterialRole.Single, "library/forge/sand/sand.albedo.png")]
    [InlineData(GeometryMaterialId.Mud, StaticTerrainMaterialRole.Single, "library/forge/mud/mud.albedo.png")]
    [InlineData(GeometryMaterialId.Snow, StaticTerrainMaterialRole.Single, "library/forge/snow/snow.albedo.png")]
    [InlineData(GeometryMaterialId.Rock, StaticTerrainMaterialRole.Single, "library/forge/rock/rock.albedo.png")]
    [InlineData(GeometryMaterialId.GrassPath, StaticTerrainMaterialRole.Single, "library/forge/grass_path/grass_path.albedo.png")]
    public void ResolveProvidesTexturePathForTerrainMaterials(
        GeometryMaterialId materialId,
        StaticTerrainMaterialRole role,
        string expectedPathSuffix)
    {
        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.Resolve(materialId, role);

        Assert.True(material.HasTexture);
        Assert.EndsWith(expectedPathSuffix.Replace('/', Path.DirectorySeparatorChar), material.TexturePath);
        Assert.True(File.Exists(material.TexturePath), $"Missing terrain texture at {material.TexturePath}");
    }

    [Fact]
    public void ResolveRolesSplitsGrassIntoTopSideAndBottom()
    {
        IReadOnlyList<StaticTerrainMaterialRole> roles = StaticTerrainMaterialCatalog.ResolveRoles(GeometryMaterialId.Grass);

        Assert.Equal(
            [StaticTerrainMaterialRole.Top, StaticTerrainMaterialRole.Side, StaticTerrainMaterialRole.Bottom],
            roles);
    }

    [Theory]
    [InlineData(GeometryMaterialId.Dirt)]
    [InlineData(GeometryMaterialId.Stone)]
    [InlineData(GeometryMaterialId.Sand)]
    [InlineData(GeometryMaterialId.Mud)]
    [InlineData(GeometryMaterialId.Snow)]
    [InlineData(GeometryMaterialId.Rock)]
    [InlineData(GeometryMaterialId.GrassPath)]
    [InlineData(GeometryMaterialId.Lamp)]
    public void ResolveRolesKeepsNonGrassMaterialsSingleTexture(GeometryMaterialId materialId)
    {
        IReadOnlyList<StaticTerrainMaterialRole> roles = StaticTerrainMaterialCatalog.ResolveRoles(materialId);

        Assert.Equal([StaticTerrainMaterialRole.Single], roles);
    }

    [Fact]
    public void Lamp_IsUntexturedWarmAndEmissive_AndTheOnlyEmissiveMaterial()
    {
        StaticTerrainMaterial lamp = StaticTerrainMaterialCatalog.Resolve(GeometryMaterialId.Lamp);

        Assert.Equal(GeometryMaterialId.Lamp, lamp.MaterialId);
        Assert.False(lamp.HasTexture);
        Assert.Equal(new OpenTK.Mathematics.Vector4(1f, 0.85f, 0.6f, 1f), lamp.BaseColor);
        Assert.Equal(new OpenTK.Mathematics.Vector3(0.9f, 0.75f, 0.5f), lamp.Emissive);
        Assert.Equal(StaticTerrainMaterialCatalog.KnownMaterialCount, Enum.GetValues<GeometryMaterialId>().Length);
        Assert.Equal(StaticTerrainMaterialCatalog.KnownMaterialCount - 1, (int)GeometryMaterialId.Lamp);
        for (int slot = 0; slot < StaticTerrainMaterialCatalog.MaterialSlotCount; slot++)
        {
            StaticTerrainMaterial material = StaticTerrainMaterialCatalog.GetBySlot(slot);
            if (material.MaterialId != GeometryMaterialId.Lamp)
            {
                Assert.Equal(OpenTK.Mathematics.Vector3.Zero, material.Emissive);
            }
        }
    }

    [Fact]
    public void Resolve_Uses_VisibleFallback_For_UnknownMaterialIds()
    {
        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.Resolve((GeometryMaterialId)999);

        Assert.False(material.HasTexture);
        Assert.Equal(new OpenTK.Mathematics.Vector4(1f, 0f, 1f, 1f), material.BaseColor);
    }
}
