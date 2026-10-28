using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    // Consistency checks across the map's parallel indexes. Mutations update several indexes
    // step by step; this makes a broken invariant visible (tests, and debug builds after
    // physics sync) instead of surfacing later as an invisible or duplicated cube.
    public IReadOnlyList<string> FindInvariantViolations()
    {
        List<string> violations = [];
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    if (Cubes[x, y, z] is not Cube cube)
                    {
                        continue;
                    }

                    GeometryCellKey key = new(x, y, z);
                    if (!_cubeCells.TryGetValue(cube, out GeometryCellKey indexed) || indexed != key)
                    {
                        violations.Add($"Cubes[{x},{y},{z}] is not indexed to its cell");
                    }

                    if (!_geometryCells.ContainsKey(key))
                    {
                        violations.Add($"Cubes[{x},{y},{z}] has no geometry record");
                    }
                }
            }
        }

        foreach ((Cube cube, GeometryCellKey key) in _cubeCells)
        {
            if (!ReferenceEquals(TryGetCubeAtCell(key.X, key.Y, key.Z), cube))
            {
                violations.Add($"cube indexed at {key} is not the occupant of that cell");
            }
        }

        if (_cubeCells.Count != _solidCubes.Count)
        {
            violations.Add($"solid cube list has {_solidCubes.Count} entries but {_cubeCells.Count} cubes are indexed");
        }

        foreach (GeometryCellRecord record in _geometryCells.Values)
        {
            if (record.FracturePieces != null)
            {
                violations.Add($"cell {record.Key} holds rubble; rubble belongs in the rubble store");
            }

            if (ShouldUseCoarseCompatibilityRender(record) && TryGetCubeAtCell(record.Key.X, record.Key.Y, record.Key.Z) == null)
            {
                violations.Add($"solid cell {record.Key} has no cube");
            }
        }

        foreach ((int id, Cube cube) in _cubesByReplicatedId)
        {
            if (!_replicatedIdsByCube.TryGetValue(cube, out int reverse) || reverse != id)
            {
                violations.Add($"replicated id {id} is not mapped back to its cube");
            }
        }

        return violations;
    }
}
