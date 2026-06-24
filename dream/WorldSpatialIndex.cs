using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class WorldSpatialIndex
{
    private const int SectorSize = 16;
    private readonly Dictionary<(int X, int Z), List<Cube>> _sectors = new();

    public void Add(Cube cube)
    {
        (int x, int z) = GetSectorKey(cube.Position);
        if (!_sectors.TryGetValue((x, z), out List<Cube>? cubes))
        {
            cubes = new List<Cube>();
            _sectors[(x, z)] = cubes;
        }

        cubes.Add(cube);
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
