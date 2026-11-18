using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Simulation.World;


public class WorldSpatialIndex
{
    private const int SectorSize = 16;
    private readonly Dictionary<(int X, int Z), List<Cube>> _sectors = new();
    private readonly Dictionary<Cube, (int X, int Z)> _cubeSectors = new();
    // Lists of sectors that emptied, reused for the next new sector so a lone cube crossing a
    // sector boundary does not allocate.
    private readonly Stack<List<Cube>> _spareSectorLists = new();

    public void Add(Cube cube)
    {
        (int x, int z) = GetSectorKey(cube.Position);
        if (!_sectors.TryGetValue((x, z), out List<Cube>? cubes))
        {
            cubes = RentSectorList();
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
            ReleaseSector(sector, cubes);
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
                    ReleaseSector(previousSector, previousCubes);
                }
            }
        }

        if (!_cubeSectors.TryGetValue(cube, out previousSector) || previousSector != newSector)
        {
            if (!_sectors.TryGetValue(newSector, out List<Cube>? currentCubes))
            {
                currentCubes = RentSectorList();
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

    // The same cubes as GetNearby, in the same order, through a struct enumerator: a per-tick caller
    // (the click-to-attack wall test) pays no iterator or enumerator allocation. Like GetNearby, the
    // index must not be changed while the result is being enumerated.
    public NearbyCubes EnumerateNearby(Vector3 position, float radius)
    {
        int sectorRadius = (int)MathF.Ceiling(radius / SectorSize);
        (int centerX, int centerZ) = GetSectorKey(position);
        return new NearbyCubes(_sectors, centerX, centerZ, sectorRadius);
    }

    public readonly struct NearbyCubes
    {
        private readonly Dictionary<(int X, int Z), List<Cube>> _sectors;
        private readonly int _centerX;
        private readonly int _centerZ;
        private readonly int _radius;

        internal NearbyCubes(Dictionary<(int X, int Z), List<Cube>> sectors, int centerX, int centerZ, int radius)
        {
            _sectors = sectors;
            _centerX = centerX;
            _centerZ = centerZ;
            _radius = radius;
        }

        public Enumerator GetEnumerator() => new(_sectors, _centerX, _centerZ, _radius);

        public struct Enumerator
        {
            private readonly Dictionary<(int X, int Z), List<Cube>> _sectors;
            private readonly int _centerX;
            private readonly int _centerZ;
            private readonly int _radius;
            private int _sectorX;
            private int _sectorZ;
            private List<Cube>.Enumerator _cubes;
            private bool _inSector;

            internal Enumerator(Dictionary<(int X, int Z), List<Cube>> sectors, int centerX, int centerZ, int radius)
            {
                _sectors = sectors;
                _centerX = centerX;
                _centerZ = centerZ;
                _radius = radius;
                _sectorX = centerX - radius;
                _sectorZ = centerZ - radius - 1;
                _cubes = default;
                _inSector = false;
            }

            public Cube Current => _cubes.Current;

            public bool MoveNext()
            {
                while (true)
                {
                    if (_inSector)
                    {
                        if (_cubes.MoveNext())
                        {
                            return true;
                        }

                        _inSector = false;
                    }

                    _sectorZ++;
                    if (_sectorZ > _centerZ + _radius)
                    {
                        _sectorZ = _centerZ - _radius;
                        _sectorX++;
                    }

                    if (_sectorX > _centerX + _radius)
                    {
                        return false;
                    }

                    if (_sectors.TryGetValue((_sectorX, _sectorZ), out List<Cube>? cubes))
                    {
                        _cubes = cubes.GetEnumerator();
                        _inSector = true;
                    }
                }
            }
        }
    }

    private List<Cube> RentSectorList()
    {
        return _spareSectorLists.TryPop(out List<Cube>? cubes) ? cubes : new List<Cube>();
    }

    // An emptied sector leaves the dictionary, so GetNearby never visits it.
    private void ReleaseSector((int X, int Z) sector, List<Cube> cubes)
    {
        _sectors.Remove(sector);
        _spareSectorLists.Push(cubes);
    }

    private static (int X, int Z) GetSectorKey(Vector3 position)
    {
        return (
            (int)MathF.Floor(position.X / SectorSize),
            (int)MathF.Floor(position.Z / SectorSize));
    }
}
