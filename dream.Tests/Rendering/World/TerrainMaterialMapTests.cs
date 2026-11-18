using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Dream.Tests.Utilities;

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

        // A hand-rolled warm-up plus one window still charges a tiering transition to the
        // measurement whenever one lands inside that window, which a loaded suite makes likely
        // however long the warm-up ran. AllocationAssert warms past the tier-up threshold and
        // keeps the cleanest of several rounds spread over time, which is the steady state.
        AllocationAssert.AtMost(0, () =>
        {
            map.Rebuild(big);
            map.Rebuild(small);
        });
    }

    [Fact]
    public void SingleCell_GivesAOneByOneByOneMap()
    {
        TerrainMaterialMap map = new();
        map.Rebuild([Cell(-3, 5, 7, GeometryMaterialId.Dirt)]);

        Assert.Equal(new Vector3i(1, 1, 1), map.Size);
        Assert.Equal(new Vector3i(-3, 5, 7), map.Origin);
        Assert.Equal(1, map.Count);
        Assert.Equal(2, map.GetTexel(-3, 5, 7));
        Assert.Equal(TerrainMaterialMap.EmptyTexel, map.GetTexel(-3, 6, 7));
    }

    [Fact]
    public void StackedFullCells_OnlyTheTopStaysExposed()
    {
        TerrainMaterialMap map = new();
        map.Rebuild(
        [
            Cell(0, 0, 0, GeometryMaterialId.Dirt),
            Cell(0, 1, 0, GeometryMaterialId.Stone),
            Cell(0, 2, 0, GeometryMaterialId.Grass)
        ]);

        Assert.Equal(TerrainMaterialMap.CoveredTexel, map.GetTexel(0, 0, 0));
        Assert.Equal(TerrainMaterialMap.CoveredTexel, map.GetTexel(0, 1, 0));
        Assert.Equal(1, map.GetTexel(0, 2, 0));
    }

    [Fact]
    public void LampOnTop_CoversTheBlendableBelow_AndStaysNonBlendable()
    {
        TerrainMaterialMap map = new();
        map.Rebuild(
        [
            Cell(0, 0, 0, GeometryMaterialId.Grass),
            Cell(0, 1, 0, GeometryMaterialId.Lamp)
        ]);

        Assert.Equal(TerrainMaterialMap.CoveredTexel, map.GetTexel(0, 0, 0));
        Assert.Equal(TerrainMaterialMap.NonBlendableTexel, map.GetTexel(0, 1, 0));
    }

    [Fact]
    public void SubCellBlockAtAnIntegerMin_WithSizeBelowOne_IsEmpty()
    {
        TerrainMaterialMap map = new();
        // min = (2, 0, 2), size 0.5: on the grid but not a full cell.
        map.Rebuild(
        [
            new StaticVoxelRenderCell(new Vector3(2.25f, 0.25f, 2.25f), new Vector3(0.5f, 0.5f, 0.5f), GeometryMaterialId.Grass),
            Cell(5, 0, 5, GeometryMaterialId.Stone)
        ]);

        Assert.Equal(new Vector3i(1, 1, 1), map.Size);
        Assert.Equal(new Vector3i(5, 0, 5), map.Origin);
        Assert.Equal(TerrainMaterialMap.EmptyTexel, map.GetTexel(2, 0, 2));
    }

    [Fact]
    public void OnlySubCellBlocks_GiveNoMap()
    {
        TerrainMaterialMap map = new();
        map.Rebuild(
        [
            new StaticVoxelRenderCell(new Vector3(2.25f, 0.25f, 2.25f), new Vector3(0.5f, 0.5f, 0.5f), GeometryMaterialId.Grass)
        ]);

        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void GetTexel_OutsideTheBounds_IsEmpty()
    {
        TerrainMaterialMap map = new();
        map.Rebuild([Cell(1, 1, 1, GeometryMaterialId.Grass)]);

        Assert.Equal(TerrainMaterialMap.EmptyTexel, map.GetTexel(0, 1, 1));
        Assert.Equal(TerrainMaterialMap.EmptyTexel, map.GetTexel(2, 1, 1));
        Assert.Equal(TerrainMaterialMap.EmptyTexel, map.GetTexel(1, 1, int.MinValue));
        Assert.Equal(TerrainMaterialMap.EmptyTexel, new TerrainMaterialMap().GetTexel(0, 0, 0));
    }
}
