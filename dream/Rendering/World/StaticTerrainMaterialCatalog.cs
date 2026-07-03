using Dream.Assets;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public static class StaticTerrainMaterialCatalog
{
    public static IReadOnlyList<StaticTerrainMaterialRole> ResolveRoles(GeometryMaterialId materialId)
    {
        return materialId == GeometryMaterialId.Grass
            ? [StaticTerrainMaterialRole.Top, StaticTerrainMaterialRole.Side, StaticTerrainMaterialRole.Bottom]
            : [StaticTerrainMaterialRole.Single];
    }

    public static StaticTerrainMaterial Resolve(
        GeometryMaterialId materialId,
        StaticTerrainMaterialRole role = StaticTerrainMaterialRole.Single)
    {
        return materialId switch
        {
            GeometryMaterialId.Grass => new StaticTerrainMaterial(
                materialId,
                role,
                new Vector4(1f, 1f, 1f, 1f),
                ResolveGrassTexture(role)),
            GeometryMaterialId.Dirt => new StaticTerrainMaterial(
                materialId,
                StaticTerrainMaterialRole.Single,
                new Vector4(1f, 1f, 1f, 1f),
                Path.Combine(
                    AssetPaths.AnimationsDirectory,
                    "library",
                    "polyhaven",
                    "park_dirt",
                    "park_dirt.diffuse.jpg")),
            GeometryMaterialId.Stone => new StaticTerrainMaterial(
                materialId,
                StaticTerrainMaterialRole.Single,
                new Vector4(1f, 1f, 1f, 1f),
                Path.Combine(
                    AssetPaths.AnimationsDirectory,
                    "library",
                    "polyhaven",
                    "ground_grey",
                    "ground_grey.diffuse.jpg")),
            _ => new StaticTerrainMaterial(
                materialId,
                StaticTerrainMaterialRole.Single,
                Vector4.One,
                string.Empty)
        };
    }

    public static StaticTerrainMaterialRole ResolveRoleForNormal(
        GeometryMaterialId materialId,
        OpenTK.Mathematics.Vector3 normal)
    {
        if (materialId != GeometryMaterialId.Grass)
        {
            return StaticTerrainMaterialRole.Single;
        }

        if (normal.Y > 0.5f)
        {
            return StaticTerrainMaterialRole.Top;
        }

        if (normal.Y < -0.5f)
        {
            return StaticTerrainMaterialRole.Bottom;
        }

        return StaticTerrainMaterialRole.Side;
    }

    private static string ResolveGrassTexture(StaticTerrainMaterialRole role)
    {
        return role switch
        {
            StaticTerrainMaterialRole.Side => AssetPaths.Animation("grass_block_side_texture.png"),
            StaticTerrainMaterialRole.Bottom => AssetPaths.Animation("Grass_bottom.jpg"),
            _ => AssetPaths.Animation("grass_top.png")
        };
    }
}
