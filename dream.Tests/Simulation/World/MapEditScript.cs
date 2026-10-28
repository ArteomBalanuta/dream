using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.World;

// A seeded random sequence of map edits covering every path that writes the geometry or rubble
// store: add, remove, move (same cell and cell change, synced or not), fracture, rubble restore,
// move and removal, subcell carving, solid-cube restore and startup-cell restore. Each step is
// chosen from the first map's state and applied by cell to every map, so maps built the same way
// (say a headless and a rendering one) receive identical edits; the results must agree.
internal static class MapEditScript
{
    private static readonly GeometryMaterialId[] Materials = Enum.GetValues<GeometryMaterialId>();

    public static void Step(Random random, IReadOnlyList<Map> maps)
    {
        Map first = maps[0];
        int operation = random.Next(11);
        switch (operation)
        {
            case 0:
            case 1:
            {
                Vector3 cell = RandomRaisedCell(random, first);
                GeometryMaterialId material = Materials[random.Next(Materials.Length)];
                bool isStatic = random.Next(4) == 0;
                ApplyToAll(maps, map => map.AddCube(cell, isStatic, withPhysics: true, material));
                break;
            }
            case 2:
            {
                if (TryPickPlacedCubeCell(random, first, out GeometryCellKey cell))
                {
                    ApplyToAll(maps, map => map.RemoveCube(map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z)!));
                }

                break;
            }
            case 3:
            case 4:
            {
                if (!TryPickPlacedCubeCell(random, first, out GeometryCellKey cell))
                {
                    break;
                }

                // Mostly small moves (same cell), some across cells; the target may be occupied.
                float reach = random.Next(3) == 0 ? 1.4f : 0.3f;
                Vector3 offset = new(
                    ((float)random.NextDouble() * 2f - 1f) * reach,
                    ((float)random.NextDouble() * 2f - 1f) * reach,
                    ((float)random.NextDouble() * 2f - 1f) * reach);
                bool syncPhysics = random.Next(2) == 0;
                ApplyToAll(maps, map =>
                {
                    Cube cube = map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z)!;
                    Vector3 before = cube.Position;
                    map.UpdateCubePosition(cube, cube.Position + offset, syncPhysics);
                    return cube.Position != before;
                });
                break;
            }
            case 5:
            {
                if (TryPickPlacedCubeCell(random, first, out GeometryCellKey cell))
                {
                    ApplyToAll(maps, map => map.FractureCubeIntoPieces(cell));
                }

                break;
            }
            case 6:
            {
                if (TryPickKey(random, first.RubbleCells, out GeometryCellKey cell))
                {
                    ApplyToAll(maps, map => map.RemoveRubble(cell));
                }

                break;
            }
            case 7:
            {
                Vector3 cell = RandomRaisedCell(random, first);
                GeometryCellKey key = new((int)cell.X, (int)cell.Y, (int)cell.Z);
                GeometryMaterialId material = Materials[random.Next(Materials.Length)];
                int count = 1 + random.Next(3);
                GeometryFracturePiece[] pieces = new GeometryFracturePiece[count];
                for (int i = 0; i < count; i++)
                {
                    pieces[i] = new GeometryFracturePiece(
                        cell + new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()),
                        0.1f + (float)random.NextDouble() * 0.4f);
                }

                ApplyToAll(maps, map => map.RestoreFracturePieces(key, material, pieces));
                break;
            }
            case 8:
            {
                if (TryPickKey(random, first.RubbleCells, out GeometryCellKey cell))
                {
                    Vector3 center = new(
                        cell.X + (float)random.NextDouble(),
                        cell.Y + (float)random.NextDouble(),
                        cell.Z + (float)random.NextDouble());
                    ApplyToAll(maps, map => map.UpdateFracturePiecePosition(cell, 0, center));
                }

                break;
            }
            case 9:
            {
                if (TryPickKey(random, first.GeometryCells, out GeometryCellKey cell) && cell.Y > 0)
                {
                    int resolution = first.GeometryCells[cell].Definition.SubdivisionResolution;
                    GeometrySubcellKey child = new(random.Next(resolution), random.Next(resolution), random.Next(resolution));
                    bool solid = random.Next(3) == 0;
                    ApplyToAll(maps, map => map.ApplyGeometrySubcellSolid(cell, child, solid).ChangedCells.Count > 0);
                }

                break;
            }
            default:
            {
                if (TryPickKey(random, first.GeometryCells, out GeometryCellKey cell))
                {
                    if (random.Next(2) == 0)
                    {
                        ApplyToAll(maps, map => map.RestoreSolidCubeCell(cell));
                    }
                    else
                    {
                        ApplyToAll(maps, map => map.RestoreGeneratedStartupCell(cell));
                    }
                }

                break;
            }
        }
    }

    private static void ApplyToAll(IReadOnlyList<Map> maps, Func<Map, bool> edit)
    {
        bool expected = edit(maps[0]);
        for (int i = 1; i < maps.Count; i++)
        {
            Assert.Equal(expected, edit(maps[i]));
        }
    }

    private static Vector3 RandomRaisedCell(Random random, Map map)
    {
        return new Vector3(
            random.Next(map.Width),
            1 + random.Next(map.Height - 1),
            random.Next(map.Depth));
    }

    private static bool TryPickPlacedCubeCell(Random random, Map map, out GeometryCellKey cell)
    {
        cell = default;
        List<GeometryCellKey> raised = [];
        IReadOnlyList<Cube> cubes = map.SolidCubes;
        for (int i = 0; i < cubes.Count; i++)
        {
            if (map.TryGetCellForCube(cubes[i], out GeometryCellKey key) && key.Y > 0)
            {
                raised.Add(key);
            }
        }

        if (raised.Count == 0)
        {
            return false;
        }

        cell = raised[random.Next(raised.Count)];
        return true;
    }

    // A random raised (Y > 0) key; floor cells are not edited by the script.
    private static bool TryPickKey(
        Random random,
        IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> cells,
        out GeometryCellKey key)
    {
        key = default;
        List<GeometryCellKey> raised = cells.Keys.Where(candidate => candidate.Y > 0).ToList();
        if (raised.Count == 0)
        {
            return false;
        }

        key = raised[random.Next(raised.Count)];
        return true;
    }
}
