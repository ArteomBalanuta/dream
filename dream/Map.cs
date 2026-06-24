using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Map
{
    private readonly WorldGrid _grid;
    private readonly WorldGenerator _generator;
    private readonly WorldPhysicsBinder? _physicsBinder;
    private readonly PhysicsIntegrator? _physics;
    private readonly List<Cube> _solidCubes = new();
    private readonly WorldSpatialIndex _placedCubeSpatialIndex = new();

    public int Width => _grid.Width;
    public int Height => _grid.Height;
    public int Depth => _grid.Depth;
    public Cube?[,,] Cubes => _grid.Cubes;
    public IReadOnlyList<Cube> SolidCubes => _solidCubes;

    public Map(
        PhysicsIntegrator? physics = null,
        bool loadVisualAssets = true,
        int width = 25,
        int height = 8,
        int depth = 25)
    {
        _physics = physics;
        _grid = new WorldGrid(width, height, depth);
        _generator = new WorldGenerator(loadVisualAssets);
        _physicsBinder = _physics != null ? new WorldPhysicsBinder(_physics) : null;
        Initialize();
    }

    private void Initialize()
    {
        for (int x = 0; x < Width; x++)
        {
            for (int z = 0; z < Depth; z++)
            {
                AddCube(new Vector3(x, 0, z), isStatic: true, withPhysics: false);
            }
        }

        _physicsBinder?.AddFloorBody(Width, Depth);

        for (int x = 0; x < Width; x++)
        {
            for (int z = 0; z < Depth; z++)
            {
                Cube? cube = Cubes[x, 0, z];
                if (cube == null || cube.Position != new Vector3(x + 0.5f, 0.5f, z + 0.5f))
                {
                    Debug.Fail($"Cube at [{x},0,{z}] misaligned! Position: {cube?.Position}");
                }
            }
        }
    }

    public void AddCube(Vector3 position, bool isStatic = false, bool withPhysics = true)
    {
        int x = (int)MathF.Floor(position.X + 1e-6f);
        int y = (int)MathF.Floor(position.Y + 1e-6f);
        int z = (int)MathF.Floor(position.Z + 1e-6f);

        if (!IsInBounds(new Vector3(x, y, z)) || Cubes[x, y, z] != null)
        {
            return;
        }

        Cube cube = _generator.CreateSolidCube(new Vector3(x, y, z));
        Cubes[x, y, z] = cube;
        _solidCubes.Add(cube);
        if (y > 0)
        {
            _placedCubeSpatialIndex.Add(cube);
        }

        if (withPhysics && _physicsBinder != null)
        {
            _physicsBinder.AddCubeBody(cube, isStatic);
        }
    }

    public void CopyPlacedCubesTo(Map target)
    {
        foreach (Cube cube in _solidCubes)
        {
            int x = (int)MathF.Floor(cube.Position.X);
            int y = (int)MathF.Floor(cube.Position.Y);
            int z = (int)MathF.Floor(cube.Position.Z);

            if (y == 0)
            {
                continue;
            }

            Vector3 cell = new Vector3(x, y, z);
            if (!target.IsInBounds(cell))
            {
                continue;
            }

            target.AddCube(cell, withPhysics: target._physics != null);
        }
    }

    public bool IsInBounds(Vector3 position)
    {
        return _grid.IsInBounds(position);
    }

    public IEnumerable<Cube> GetNearbySolidCubes(Vector3 position, float radius)
    {
        return GetRenderCandidateCubes(position, radius);
    }

    public IEnumerable<Cube> GetRenderCandidateCubes(Vector3 position, float radius)
    {
        foreach (Cube cube in GetNearbyFloorCubes(position, radius))
        {
            yield return cube;
        }

        foreach (Cube cube in GetNearbyPlacedCubes(position, radius))
        {
            yield return cube;
        }
    }

    private IEnumerable<Cube> GetNearbyPlacedCubes(Vector3 position, float radius)
    {
        return _placedCubeSpatialIndex.GetNearby(position, radius);
    }

    private IEnumerable<Cube> GetNearbyFloorCubes(Vector3 position, float radius)
    {
        int minX = Math.Max(0, (int)MathF.Floor(position.X - radius));
        int maxX = Math.Min(Width - 1, (int)MathF.Ceiling(position.X + radius));
        int minZ = Math.Max(0, (int)MathF.Floor(position.Z - radius));
        int maxZ = Math.Min(Depth - 1, (int)MathF.Ceiling(position.Z + radius));

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                Cube? cube = Cubes[x, 0, z];
                if (cube != null)
                {
                    yield return cube;
                }
            }
        }
    }
}
