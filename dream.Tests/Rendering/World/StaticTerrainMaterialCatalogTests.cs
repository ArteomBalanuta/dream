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
    public void ResolveProvidesTexturePathForTerrainMaterials(
        GeometryMaterialId materialId,
        StaticTerrainMaterialRole role,
        string expectedPathSuffix)
    {
        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.Resolve(materialId, role);

        Assert.True(material.HasTexture);
        Assert.EndsWith(expectedPathSuffix.Replace('/', Path.DirectorySeparatorChar), material.TexturePath);
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
    public void ResolveRolesKeepsNonGrassMaterialsSingleTexture(GeometryMaterialId materialId)
    {
        IReadOnlyList<StaticTerrainMaterialRole> roles = StaticTerrainMaterialCatalog.ResolveRoles(materialId);

        Assert.Equal([StaticTerrainMaterialRole.Single], roles);
    }
}
