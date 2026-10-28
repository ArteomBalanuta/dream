using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    // A cell the map treats as fully occupied (CanTransitionCubeToCell rejects any other cube)
    // whose static collision is only its solid microcells needs a support, so dynamic cubes are
    // blocked by the whole cell (WorldPhysicsBinder.SyncCellSupportBody). Floor cells (y == 0)
    // sit inside the floor body, and fractured cells are left to their dynamic pieces.
    private bool NeedsCellSupport(GeometryCellKey key)
    {
        return key.Y > 0 &&
            _staticPhysicsCells.Contains(key) &&
            Cubes[key.X, key.Y, key.Z] != null &&
            _geometryCells.TryGetValue(key, out GeometryCellRecord? record) &&
            record.SubdivisionState != null &&
            record.FracturePieces is not { Count: > 0 };
    }

    // Test hook: whether the cell currently has a support body.
    internal bool HasCellSupportBody(GeometryCellKey key)
    {
        return _physicsBinder != null && _physicsBinder.TryGetCellSupportBody(key, out _);
    }

    internal int CellSupportBodyCount => _physicsBinder?.CellSupportBodyCount ?? 0;
}
