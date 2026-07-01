using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Simulation.World;


public class WorldSpatialIndex
{
    private const int SectorSize = 16;
    private readonly Dictionary<(int X, int Z), List<Cube>> _sectors = new();
    private readonly Dictionary<Cube, (int X, int Z)> _cubeSectors = new();

    public void Add(Cube cube)
    {
        (int x, int z) = GetSectorKey(cube.Position);
        if (!_sectors.TryGetValue((x, z), out List<Cube>? cubes))
        {
            cubes = new List<Cube>();
            _sectors[(x, z)] = cubes;
        }

        cubes.Add(cube);
        _cubeSectors[cube] = (x, z);
    }

    public bool Remove(Cube cube)
    {
        if (!_cubeSectors.TryGetValue(cube, out (int X, int Z) sector))
        {
            return false;
        }

        if (!_sectors.TryGetValue(sector, out List<Cube>? cubes))
        {
            _cubeSectors.Remove(cube);
            return false;
        }

        bool removed = cubes.Remove(cube);
        if (removed && cubes.Count == 0)
        {
            _sectors.Remove(sector);
        }

        if (removed)
        {
            _cubeSectors.Remove(cube);
        }

        return removed;
    }

    public void UpdatePosition(Cube cube, Vector3 position)
    {
        (int X, int Z) newSector = GetSectorKey(position);
        if (_cubeSectors.TryGetValue(cube, out (int X, int Z) previousSector) && previousSector != newSector)
        {
            if (_sectors.TryGetValue(previousSector, out List<Cube>? previousCubes))
            {
                previousCubes.Remove(cube);
                if (previousCubes.Count == 0)
                {
                    _sectors.Remove(previousSector);
                }
            }
        }

        if (!_cubeSectors.TryGetValue(cube, out previousSector) || previousSector != newSector)
        {
            if (!_sectors.TryGetValue(newSector, out List<Cube>? currentCubes))
            {
                currentCubes = new List<Cube>();
                _sectors[newSector] = currentCubes;
            }

            if (!currentCubes.Contains(cube))
            {
                currentCubes.Add(cube);
            }
        }

        _cubeSectors[cube] = newSector;
    }

    public IEnumerable<Cube> GetNearby(Vector3 position, float radius)
    {
        int sectorRadius = (int)MathF.Ceiling(radius / SectorSize);
        (int centerX, int centerZ) = GetSectorKey(position);

        for (int sectorX = centerX - sectorRadius; sectorX <= centerX + sectorRadius; sectorX++)
        {
            for (int sectorZ = centerZ - sectorRadius; sectorZ <= centerZ + sectorRadius; sectorZ++)
            {
                if (!_sectors.TryGetValue((sectorX, sectorZ), out List<Cube>? cubes))
                {
                    continue;
                }

                foreach (Cube cube in cubes)
                {
                    yield return cube;
                }
            }
        }
    }

    private static (int X, int Z) GetSectorKey(Vector3 position)
    {
        return (
            (int)MathF.Floor(position.X / SectorSize),
            (int)MathF.Floor(position.Z / SectorSize));
    }
}
