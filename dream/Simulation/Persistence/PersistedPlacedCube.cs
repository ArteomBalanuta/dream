using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Simulation.Persistence;

public readonly record struct PersistedPlacedCube(
    int CubeId,
    string OwnerPlayerName,
    Vector3 CellPosition,
    GeometryShapeKind ShapeKind,
    GeometryMaterialId MaterialId,
    int CurrentHealth,
    int MaxHealth,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Vector3? CenterPosition = null);
