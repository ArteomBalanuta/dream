using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class TerrainMaterialMapTests
{
    private static StaticVoxelRenderCell Cell(int x, int y, int z, GeometryMaterialId material)
    {
        return new StaticVoxelRenderCell(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), Vector3.One, material);
    }

    [Fact]
    public void NoCells_GivesNoMap()
    {
        TerrainMaterialMap map = new();
        map.Rebuild([]);

        Assert.Equal(Vector3i.Zero, map.Size);
        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void ExposedCell_StoresMaterialIdPlusOne()
    {
        TerrainMaterialMap map = new();
        map.Rebuild(
        [
            Cell(2, 3, 4, GeometryMaterialId.Stone),
            Cell(3, 3, 4, GeometryMaterialId.GrassPath),
            Cell(2, 3, 5, GeometryMaterialId.Grass)
        ]);

        Assert.Equal(new Vector3i(2, 1, 2), map.Size);
        Assert.Equal(new Vector3i(2, 3, 4), map.Origin);
        Assert.Equal(3, map.GetTexel(2, 3, 4));
        Assert.Equal(8, map.GetTexel(3, 3, 4));
        Assert.Equal(1, map.GetTexel(2, 3, 5));
        Assert.Equal(0, map.GetTexel(3, 3, 5));
        // Layout: i + sizeX * (j + sizeY * k).
        Assert.Equal(1, map.Texels[0 + (2 * (0 + (1 * 1)))]);
    }

    [Fact]
    public void CoveredCell_IsMarked254_AndTheTopStaysExposed()
    {
        TerrainMaterialMap map = new();
        map.Rebuild(
        [
            Cell(0, 0, 0, GeometryMaterialId.Dirt),
            Cell(0, 1, 0, GeometryMaterialId.Grass)
        ]);

        Assert.Equal(TerrainMaterialMap.CoveredTexel, map.GetTexel(0, 0, 0));
        Assert.Equal(1, map.GetTexel(0, 1, 0));
    }

    [Fact]
    public void LampIsNonBlendable_AndStillCoversTheCellBelow()
    {
        TerrainMaterialMap map = new();
        map.Rebuild(
        [
            Cell(0, 0, 0, GeometryMaterialId.Stone),
            Cell(0, 1, 0, GeometryMaterialId.Lamp)
        ]);

        Assert.Equal(TerrainMaterialMap.CoveredTexel, map.GetTexel(0, 0, 0));
        Assert.Equal(TerrainMaterialMap.NonBlendableTexel, map.GetTexel(0, 1, 0));
    }

    [Fact]
    public void PartialBlock_IsEmpty_AndDoesNotCoverTheCellBelow()
    {
        TerrainMaterialMap map = new();
        StaticVoxelRenderCell slab = new(new Vector3(0.5f, 1.25f, 0.5f), new Vector3(1f, 0.5f, 1f), GeometryMaterialId.Stone);
        StaticVoxelRenderCell offGrid = new(new Vector3(1.0f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Stone);
        map.Rebuild([Cell(0, 0, 0, GeometryMaterialId.Grass), slab, offGrid]);

        Assert.Equal(new Vector3i(1, 1, 1), map.Size);
        Assert.Equal(1, map.GetTexel(0, 0, 0));
        Assert.Equal(0, map.GetTexel(0, 1, 0));
    }

    [Fact]
    public void NegativeOrigin_IsSupported()
    {
        TerrainMaterialMap map = new();
        map.Rebuild(
        [
            Cell(-3, -1, -2, GeometryMaterialId.Sand),
            Cell(-2, -1, -2, GeometryMaterialId.Mud)
        ]);

        Assert.Equal(new Vector3i(-3, -1, -2), map.Origin);
        Assert.Equal(4, map.GetTexel(-3, -1, -2));
        Assert.Equal(5, map.GetTexel(-2, -1, -2));
        Assert.Equal(0, map.GetTexel(-4, -1, -2));
    }

    [Fact]
    public void SizeAtTheCapIsKept_AndOneOverGivesNoMap()
    {
        TerrainMaterialMap map = new();
        map.Rebuild([Cell(0, 0, 0, GeometryMaterialId.Grass), Cell(255, 0, 0, GeometryMaterialId.Grass)]);
        Assert.Equal(new Vector3i(256, 1, 1), map.Size);

        map.Rebuild([Cell(0, 0, 0, GeometryMaterialId.Grass), Cell(256, 0, 0, GeometryMaterialId.Grass)]);
        Assert.Equal(Vector3i.Zero, map.Size);

        map.Rebuild([Cell(0, 0, 0, GeometryMaterialId.Grass), Cell(0, 0, 256, GeometryMaterialId.Grass)]);
        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void RebuildClearsStaleTexels_AndVersionIncrements()
    {
        TerrainMaterialMap map = new();
        map.Rebuild([Cell(0, 0, 0, GeometryMaterialId.Grass), Cell(3, 0, 0, GeometryMaterialId.Grass)]);
        int version = map.Version;

        map.Rebuild([Cell(0, 0, 0, GeometryMaterialId.Stone)]);

        Assert.True(map.Version > version);
        Assert.Equal(3, map.GetTexel(0, 0, 0));
        Assert.Equal(0, map.GetTexel(3, 0, 0));

        version = map.Version;
        map.Reset();
        Assert.True(map.Version > version);
        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void RebuildOfTheSameOrASmallerWorld_AllocatesNothing()
    {
        List<StaticVoxelRenderCell> big = [];
        for (int x = 0; x < 40; x++)
        {
            for (int z = 0; z < 40; z++)
            {
                big.Add(Cell(x, 0, z, GeometryMaterialId.Grass));
                big.Add(Cell(x, 1, z, GeometryMaterialId.Dirt));
            }
        }

        StaticVoxelRenderCell[] small = [Cell(0, 0, 0, GeometryMaterialId.Grass), Cell(5, 2, 5, GeometryMaterialId.Stone)];
        TerrainMaterialMap map = new();
        // Warm up past tiered JIT so only the steady state is measured.
        for (int i = 0; i < 300; i++)
        {
            map.Rebuild(big);
            map.Rebuild(small);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            map.Rebuild(big);
            map.Rebuild(small);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
