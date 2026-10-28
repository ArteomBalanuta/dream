using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticTerrainMaterialCatalogTests
{
    [Theory]
    [InlineData(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Top, "grass_top.png")]
    [InlineData(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Side, "grass_block_side_texture.png")]
    [InlineData(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Bottom, "Grass_bottom.jpg")]
    [InlineData(GeometryMaterialId.Dirt, StaticTerrainMaterialRole.Single, "library/polyhaven/park_dirt/park_dirt.diffuse.jpg")]
    [InlineData(GeometryMaterialId.Stone, StaticTerrainMaterialRole.Single, "library/polyhaven/ground_grey/ground_grey.diffuse.jpg")]
    [InlineData(GeometryMaterialId.Sand, StaticTerrainMaterialRole.Single, "library/polyhaven/sand_01/sand_01.diffuse.jpg")]
    [InlineData(GeometryMaterialId.Mud, StaticTerrainMaterialRole.Single, "library/polyhaven/brown_mud/brown_mud.diffuse.jpg")]
    [InlineData(GeometryMaterialId.Snow, StaticTerrainMaterialRole.Single, "library/polyhaven/snow_04/snow_04.diffuse.jpg")]
    [InlineData(GeometryMaterialId.Rock, StaticTerrainMaterialRole.Single, "library/polyhaven/rocks_ground_08/rocks_ground_08.diffuse.jpg")]
    [InlineData(GeometryMaterialId.GrassPath, StaticTerrainMaterialRole.Single, "library/polyhaven/grass_path_3/grass_path_3.diffuse.jpg")]
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
