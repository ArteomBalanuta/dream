namespace Dream.Simulation.World.Geometry;

// The materials a player may pick in the building menu (the "build.material" action), and the
// material a build places. A selection of null is Auto: the material of the supporting cell, as
// before the menu existed, except that Auto never copies a lamp (a lamp on a lamp would spread
// lights with no choice made) and falls back to Stone on top of one.
public static class BuildMaterialCatalog
{
    // The menu order. Shared and read-only; callers must not mutate it.
    public static readonly IReadOnlyList<GeometryMaterialId> Placeable =
        [GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Lamp];

    // Auto with no supporting cell (the ground plane) keeps the historical Grass.
    public const GeometryMaterialId AutoWithoutSupport = GeometryMaterialId.Grass;

    public const GeometryMaterialId AutoFallback = GeometryMaterialId.Stone;

    public static bool IsPlaceable(GeometryMaterialId materialId)
    {
        return materialId is GeometryMaterialId.Stone or GeometryMaterialId.Dirt or GeometryMaterialId.Lamp;
    }

    // A null selection is always valid (Auto); otherwise only a placeable material is.
    public static bool IsValidSelection(GeometryMaterialId? selection)
    {
        return selection is not GeometryMaterialId materialId || IsPlaceable(materialId);
    }

    public static GeometryMaterialId ResolveAuto(GeometryMaterialId? supportMaterial)
    {
        return supportMaterial switch
        {
            null => AutoWithoutSupport,
            GeometryMaterialId.Lamp => AutoFallback,
            GeometryMaterialId materialId => materialId
        };
    }

    public static GeometryMaterialId ResolvePlacement(GeometryMaterialId? selection, GeometryMaterialId? supportMaterial)
    {
        return selection is GeometryMaterialId selected && IsPlaceable(selected)
            ? selected
            : ResolveAuto(supportMaterial);
    }

    public static string DisplayName(GeometryMaterialId? selection)
    {
        return selection switch
        {
            null => "Auto",
            GeometryMaterialId.Stone => "Stone",
            GeometryMaterialId.Dirt => "Dirt",
            GeometryMaterialId.Lamp => "Lamp",
            GeometryMaterialId materialId => materialId.ToString()
        };
    }
}
