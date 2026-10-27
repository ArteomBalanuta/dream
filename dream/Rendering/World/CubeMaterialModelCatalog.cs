using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public static class CubeMaterialModelCatalog
{
    // Null for an unmapped material, and also while a mapped model is still loading (or failed),
    // so the caller draws the untextured fallback cube until the model is ready. Never blocks.
    public static AnimatedModel? Resolve(GeometryMaterialId materialId)
    {
        string? path = materialId switch
        {
            GeometryMaterialId.Dirt => ModelAssets.DirtCubePath,
            GeometryMaterialId.Stone => ModelAssets.StoneCubePath,
            GeometryMaterialId.Grass => ModelAssets.GrassCubePath,
            _ => null
        };

        return path != null && ModelAssets.TryGet(path, out AnimatedModel? model) ? model : null;
    }
}
