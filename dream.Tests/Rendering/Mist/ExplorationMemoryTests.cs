using Dream.Rendering.Mist;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

public class ExplorationMemoryTests
{
    private static byte At(ExplorationMemory memory, int x, int z) => memory.Cells[z * memory.Width + x];

    [Fact]
    public void A_New_Memory_Is_Unseen_And_Dirty_For_The_First_Upload()
    {
        ExplorationMemory memory = new(20, 12);

        Assert.Equal(20 * 12, memory.Cells.Length);
        Assert.All(memory.Cells, cell => Assert.Equal(0, cell));
        Assert.True(memory.Dirty);
    }

    [Fact]
    public void Reveal_Is_Full_Inside_Eight_Metres_Soft_To_Ten_And_Nothing_Beyond()
    {
        ExplorationMemory memory = new(60, 60);

        Assert.True(memory.Reveal(30f, 30f));

        Assert.Equal(255, At(memory, 30, 30));
        Assert.Equal(255, At(memory, 30 + 7, 30)); // 7.5 m from the cell centre of (30,30) pose 30.0
        Assert.Equal(0, At(memory, 30 + 10, 30)); // 10.5 m
        Assert.Equal(0, At(memory, 5, 5));
        byte soft = At(memory, 30 + 8, 30); // 8.5 m: inside the soft band
        Assert.InRange(soft, 1, 254);
        Assert.True(At(memory, 30 + 9, 30) < soft); // 9.5 m, fainter
    }

    [Fact]
    public void Reveal_Only_Raises_A_Cell_Never_Lowers_It()
    {
        ExplorationMemory memory = new(60, 60);
        memory.Reveal(10f, 30f);
        byte before = At(memory, 18, 30);
        Assert.InRange(before, 1, 254);

        memory.Reveal(40f, 30f);

        Assert.Equal(before, At(memory, 18, 30));
        Assert.Equal(255, At(memory, 40, 30));
        // Revealing from the near side raises the soft cell.
        memory.Reveal(14f, 30f);
        Assert.Equal(255, At(memory, 18, 30));
    }

    [Fact]
    public void A_Move_Under_Half_A_Metre_Does_Not_Reveal_Again()
    {
        ExplorationMemory memory = new(60, 60);
        memory.Reveal(30f, 30f);
        memory.ClearDirty();

        Assert.False(memory.Reveal(30.3f, 30f));
        Assert.False(memory.Dirty);
        Assert.True(memory.Reveal(30.6f, 30f));
        Assert.True(memory.Dirty);
    }

    [Fact]
    public void Dirty_Is_Raised_Only_When_A_Cell_Changed()
    {
        ExplorationMemory memory = new(60, 60);
        memory.RevealAll();
        memory.ClearDirty();

        // The stamp runs (the player moved) but every cell is already remembered: nothing to upload.
        Assert.True(memory.Reveal(30f, 30f));
        Assert.False(memory.Dirty);

        ExplorationMemory fresh = new(60, 60);
        fresh.ClearDirty();
        Assert.True(fresh.Reveal(30f, 30f));
        Assert.True(fresh.Dirty);
    }

    [Fact]
    public void Reset_Forgets_Everything_Resizes_And_Allows_The_Next_Reveal()
    {
        ExplorationMemory memory = new(30, 30);
        memory.Reveal(15f, 15f);
        memory.ClearDirty();

        memory.Reset(40, 25);

        Assert.Equal(40, memory.Width);
        Assert.Equal(25, memory.Depth);
        Assert.Equal(40 * 25, memory.Cells.Length);
        Assert.All(memory.Cells, cell => Assert.Equal(0, cell));
        Assert.True(memory.Dirty);
        Assert.True(memory.Reveal(15f, 15f)); // not suppressed by the pre-reset position
    }

    [Fact]
    public void Reset_To_The_Same_Size_Reuses_The_Buffer()
    {
        ExplorationMemory memory = new(30, 30);
        byte[] cells = memory.Cells;
        memory.Reveal(15f, 15f);

        memory.Reset(30, 30);

        Assert.Same(cells, memory.Cells);
        Assert.All(memory.Cells, cell => Assert.Equal(0, cell));
    }

    [Theory]
    [InlineData(-3f, -3f)]
    [InlineData(0f, 0f)]
    [InlineData(29.9f, 29.9f)]
    [InlineData(500f, 500f)]
    public void Reveal_Near_Or_Past_The_Edge_Clips_To_The_Map(float x, float z)
    {
        ExplorationMemory memory = new(30, 30);

        memory.Reveal(x, z);

        Assert.Equal(900, memory.Cells.Length);
    }

    [Fact]
    public void Reveal_At_A_Corner_Marks_The_Corner_Cell()
    {
        ExplorationMemory memory = new(30, 30);

        memory.Reveal(0.5f, 0.5f);

        Assert.Equal(255, At(memory, 0, 0));
        Assert.Equal(0, At(memory, 29, 29));
    }

    [Fact]
    public void RevealAll_Fills_The_Map()
    {
        ExplorationMemory memory = new(30, 30);
        memory.ClearDirty();

        memory.RevealAll();

        Assert.All(memory.Cells, cell => Assert.Equal(255, cell));
        Assert.True(memory.Dirty);
    }

    [Fact]
    public void Revealing_Allocates_Nothing_After_Construction()
    {
        ExplorationMemory memory = new(100, 100);
        memory.Reveal(0f, 0f);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 1; i < 400; i++)
        {
            memory.Reveal(i * 0.25f % 100f, i * 0.6f % 100f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
