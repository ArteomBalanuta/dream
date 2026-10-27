using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public static class CubeMaterialModelCatalog
{
    public static AnimatedModel? Resolve(GeometryMaterialId materialId)
    {
        return materialId switch
        {
            GeometryMaterialId.Dirt => ModelAssets.DirtCube,
            GeometryMaterialId.Stone => ModelAssets.StoneCube,
            GeometryMaterialId.Grass => ModelAssets.GrassCube,
            _ => null
        };
    }
}
