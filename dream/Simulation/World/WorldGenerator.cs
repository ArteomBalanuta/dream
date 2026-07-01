using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Rendering.Core;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public class WorldGenerator
{
    private const int DefaultSubdivisionResolution = 10;
    private readonly bool _loadVisualAssets;

    public WorldGenerator(bool loadVisualAssets)
    {
        _loadVisualAssets = loadVisualAssets;
    }

    public GeometryCellRecord CreateGeometryCell(Vector3 cellPosition, bool isFloor)
    {
        GeometryCellKey key = new GeometryCellKey(
            (int)cellPosition.X,
            (int)cellPosition.Y,
            (int)cellPosition.Z);

        GeometryCellDefinition definition = new GeometryCellDefinition(
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Grass,
            DefaultSubdivisionResolution,
            IsSolid: true,
            IsFloor: isFloor);

        return new GeometryCellRecord(key, definition);
    }

    public Cube CreateCompatibilityCube(GeometryCellRecord geometryCell)
    {
        return new Cube
        {
            Position = new Vector3(geometryCell.Key.X + 0.5f, geometryCell.Key.Y + 0.5f, geometryCell.Key.Z + 0.5f),
            IsEmpty = false,
            IsSolid = true,
            Animation = _loadVisualAssets ? ModelAssets.GrassCube : null,
            ShaderName = "cube"
        };
    }

    public GeometryRenderBlockDescriptor CreateRenderBlockDescriptor(
        GeometryCellRecord geometryCell,
        Vector3 center,
        float sideLength,
        GeometrySubcellKey? subcellKey = null)
    {
        return new GeometryRenderBlockDescriptor(
            geometryCell.Key,
            center,
            sideLength,
            geometryCell.Definition.MaterialId,
            _loadVisualAssets ? ModelAssets.GrassCube : null,
            "cube",
            subcellKey);
    }

    public Cube CreateSolidCube(Vector3 cellPosition)
    {
        return CreateCompatibilityCube(CreateGeometryCell(cellPosition, isFloor: cellPosition.Y <= 0f));
    }
}
