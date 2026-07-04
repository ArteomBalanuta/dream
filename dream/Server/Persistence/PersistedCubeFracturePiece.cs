using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Server.Persistence;

public readonly record struct PersistedCubeFracturePiece(
    int SourceCubeId,
    int PieceIndex,
    string OwnerPlayerName,
    Vector3 ParentCell,
    Vector3 Center,
    float SideLength,
    GeometryMaterialId MaterialId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
