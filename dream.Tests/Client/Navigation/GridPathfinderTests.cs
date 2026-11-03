using Dream.Client.Navigation;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Navigation;

public class GridPathfinderTests
{
    // Floor of support cubes at y 0; level 1 is the walking level.
    internal sealed class FakeWorld : INavWorld
    {
        private readonly bool[,,] _cells;

        public FakeWorld(int size = 12, int height = 10)
        {
            Width = size;
            Depth = size;
            Height = height;
            _cells = new bool[size, height, size];
            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    _cells[x, 0, z] = true;
                }
            }
        }

        public int Width { get; }

        public int Height { get; }

        public int Depth { get; }

        public void Set(int x, int y, int z, bool solid = true) => _cells[x, y, z] = solid;

        public void Wall(int x, int z0, int z1, int fromY, int toY)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int y = fromY; y <= toY; y++)
                {
                    _cells[x, y, z] = true;
                }
            }
        }

        public bool IsBlocked(int x, int y, int z)
            => x < 0 || y < 0 || z < 0 || x >= Width || y >= Height || z >= Depth || _cells[x, y, z];

        public bool IsSupport(int x, int y, int z) => IsBlocked(x, y, z);
    }

    private static (bool Found, bool Reached, List<NavCell> Path) Find(FakeWorld world, NavCell start, NavCell goal)
    {
        List<NavCell> path = [];
        bool found = new GridPathfinder().TryFindPath(world, start, goal, path, out bool reached);
        return (found, reached, path);
    }

    [Fact]
    public void PathsAroundATwoHighWall()
    {
        FakeWorld world = new();
        world.Wall(5, 0, 8, 1, 2);
        var (found, reached, path) = Find(world, new NavCell(2, 1, 4), new NavCell(8, 1, 4));

        Assert.True(found);
        Assert.True(reached);
        Assert.Equal(new NavCell(8, 1, 4), path[^1]);
        Assert.All(path, cell => Assert.Equal(1, cell.Level));
        Assert.Contains(path, cell => cell.Z >= 9);
        Assert.DoesNotContain(path, cell => cell.X == 5 && cell.Z <= 8);
    }

    [Fact]
    public void StepsUpAOneHighWall()
    {
        FakeWorld world = new();
        world.Wall(5, 0, 11, 1, 1);
        var (_, reached, path) = Find(world, new NavCell(2, 1, 4), new NavCell(8, 1, 4));

        Assert.True(reached);
        Assert.Equal(new NavCell(8, 1, 4), path[^1]);
        Assert.Contains(path, cell => cell.X == 5 && cell.Level == 2);
    }

    [Fact]
    public void ClimbsOntoAOneHighBlockAndDropsOffTheFarSide()
    {
        FakeWorld world = new();
        world.Wall(5, 4, 4, 1, 1);
        world.Wall(6, 4, 4, 1, 1);
        var (_, reached, path) = Find(world, new NavCell(4, 1, 4), new NavCell(7, 1, 4));

        Assert.True(reached);
        Assert.Contains(new NavCell(5, 2, 4), path);
    }

    [Fact]
    public void DropsAnyHeight()
    {
        FakeWorld world = new();
        for (int x = 2; x <= 3; x++)
        {
            world.Wall(x, 3, 5, 1, 3);
        }

        var (_, reached, path) = Find(world, new NavCell(2, 4, 4), new NavCell(6, 1, 4));

        Assert.True(reached);
        Assert.Equal(new NavCell(6, 1, 4), path[^1]);
        Assert.Contains(path, cell => cell.Level == 4);
    }

    [Fact]
    public void WalksThroughAOneWideDoor_Centred()
    {
        FakeWorld world = new();
        world.Wall(5, 0, 11, 1, 2);
        world.Set(5, 1, 6, false);
        world.Set(5, 2, 6, false);
        var (_, reached, path) = Find(world, new NavCell(2, 1, 6), new NavCell(8, 1, 6));

        Assert.True(reached);
        Assert.Contains(new NavCell(5, 1, 6), path);
    }

    [Fact]
    public void NeverCutsACorner()
    {
        FakeWorld world = new();
        world.Wall(5, 5, 5, 1, 2);
        world.Wall(6, 6, 6, 1, 2);
        var (_, reached, path) = Find(world, new NavCell(5, 1, 6), new NavCell(6, 1, 5));

        Assert.True(reached);
        for (int i = 1; i < path.Count; i++)
        {
            int dx = path[i].X - path[i - 1].X;
            int dz = path[i].Z - path[i - 1].Z;
            if (dx != 0 && dz != 0)
            {
                Assert.False(world.IsBlocked(path[i - 1].X + dx, 1, path[i - 1].Z));
                Assert.False(world.IsBlocked(path[i - 1].X, 1, path[i - 1].Z + dz));
            }
        }
    }

    [Fact]
    public void WalledInGoal_ReturnsNearestCellAndNotReached()
    {
        FakeWorld world = new();
        for (int x = 7; x <= 9; x++)
        {
            for (int z = 7; z <= 9; z++)
            {
                if (x != 8 || z != 8)
                {
                    world.Wall(x, z, z, 1, 3);
                }
            }
        }

        var (found, reached, path) = Find(world, new NavCell(2, 1, 8), new NavCell(8, 1, 8));

        Assert.True(found);
        Assert.False(reached);
        Assert.NotEqual(new NavCell(8, 1, 8), path[^1]);
        int dx = path[^1].X - 8;
        int dz = path[^1].Z - 8;
        Assert.Equal(4, (dx * dx) + (dz * dz));
        Assert.Equal(new NavCell(2, 1, 8), path[0]);
    }

    [Fact]
    public void NoStandableStart_ReturnsFalse()
    {
        FakeWorld world = new();
        world.Wall(2, 4, 4, 1, 1);
        var (found, _, path) = Find(world, new NavCell(2, 1, 4), new NavCell(8, 1, 4));

        Assert.False(found);
        Assert.Empty(path);
    }

    [Theory]
    [InlineData(12, 5)]
    [InlineData(-1, 5)]
    [InlineData(5, 12)]
    [InlineData(12, 12)]
    public void OutOfBoundsStartBesideTheEdgeResolvesToTheNearestStandableCell(int startX, int startZ)
    {
        FakeWorld world = new();
        GridPathfinder finder = new();
        List<NavCell> path = [];

        bool found = finder.TryFindPath(world, new NavCell(startX, 1, startZ), new NavCell(6, 1, 6), path, out bool reached);

        Assert.True(found);
        Assert.True(reached);
        Assert.True(finder.StartRelocated);
        Assert.InRange(path[0].X, 0, 11);
        Assert.InRange(path[0].Z, 0, 11);
        Assert.True(Math.Max(Math.Abs(path[0].X - Math.Clamp(startX, 0, 11)), Math.Abs(path[0].Z - Math.Clamp(startZ, 0, 11))) == 0);
        Assert.Equal(new NavCell(6, 1, 6), path[^1]);
    }

    [Fact]
    public void StartTooFarOutsideTheGridStillFails()
    {
        Assert.False(Find(new FakeWorld(), new NavCell(30, 1, 5), new NavCell(6, 1, 6)).Found);
    }
}
