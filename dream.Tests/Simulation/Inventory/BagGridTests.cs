using System.Numerics;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Simulation.Inventory;

// Classic HUD iteration 2, C0a: the 8x8 footprint bag board. BagGrid is pure, so these tests build
// boards by hand and, for the random sweep, check every verdict against a slow reference written with
// plain cell grids instead of bit masks.
public sealed class BagGridTests
{
    private static ItemFootprint F(int cols, int rows) => new((byte)cols, (byte)rows);

    // ---- constants ----

    [Fact]
    public void TheConstants_ArePlanned()
    {
        Assert.Equal(8, BagGrid.Columns);
        Assert.Equal(8, BagGrid.Rows);
        Assert.Equal(64, BagGrid.CellCount);
        Assert.Equal(4, BagGrid.MaxFootprint);
        Assert.Equal(new ItemFootprint(1, 1), ItemFootprint.One);
    }

    // ---- TryMask ----

    [Fact]
    public void AOneByOne_AtEveryAnchor_IsExactlyThatBit()
    {
        for (int anchor = 0; anchor < 64; anchor++)
        {
            Assert.True(BagGrid.TryMask(anchor, ItemFootprint.One, out ulong mask));
            Assert.Equal(1UL << anchor, mask);
        }
    }

    [Theory]
    [InlineData(0, 2, 3, 0x0000_0000_0003_0303UL)]
    [InlineData(0, 4, 4, 0x0000_0000_0F0F_0F0FUL)]
    [InlineData(0, 4, 1, 0x0000_0000_0000_000FUL)]
    [InlineData(36, 4, 4, 0xF0F0_F0F0_0000_0000UL)]
    [InlineData(9, 2, 2, 0x0000_0000_0006_0600UL)]
    [InlineData(63, 1, 1, 0x8000_0000_0000_0000UL)]
    [InlineData(62, 2, 1, 0xC000_0000_0000_0000UL)]
    [InlineData(55, 1, 2, 0x8080_0000_0000_0000UL)]
    [InlineData(54, 2, 2, 0xC0C0_0000_0000_0000UL)]
    [InlineData(7, 1, 4, 0x0000_0000_8080_8080UL)]
    [InlineData(56, 4, 1, 0x0F00_0000_0000_0000UL)]
    [InlineData(60, 4, 1, 0xF000_0000_0000_0000UL)]
    public void KnownMasks_AreExact(int anchor, int cols, int rows, ulong expected)
    {
        Assert.True(BagGrid.TryMask(anchor, F(cols, rows), out ulong mask));
        Assert.Equal(expected, mask);
    }

    [Fact]
    public void EveryFootprintAtEveryAnchor_FitsExactlyWhenItStaysOnTheBoard()
    {
        int fitted = 0;
        for (int cols = 1; cols <= BagGrid.MaxFootprint; cols++)
        {
            for (int rows = 1; rows <= BagGrid.MaxFootprint; rows++)
            {
                for (int anchor = 0; anchor < BagGrid.CellCount; anchor++)
                {
                    int col = anchor % 8;
                    int row = anchor / 8;
                    bool inside = col + cols <= 8 && row + rows <= 8;
                    bool ok = BagGrid.TryMask(anchor, F(cols, rows), out ulong mask);
                    Assert.Equal(inside, ok);
                    if (!inside)
                    {
                        Assert.Equal(0UL, mask);
                        continue;
                    }

                    fitted++;
                    Assert.Equal(cols * rows, BitOperations.PopCount(mask));

                    // Every bit is a cell of the rectangle: nothing wrapped into the next row.
                    for (int cell = 0; cell < 64; cell++)
                    {
                        bool set = (mask & (1UL << cell)) != 0;
                        bool expected = cell % 8 >= col && cell % 8 < col + cols && cell / 8 >= row && cell / 8 < row + rows;
                        Assert.Equal(expected, set);
                    }
                }
            }
        }

        // 1x1 .. 4x4 on 8x8: sum over w,h of (9-w)(9-h) = (8+7+6+5)^2.
        Assert.Equal(26 * 26, fitted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(100)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AnAnchorOffTheBoard_HasNoMask(int anchor)
    {
        Assert.False(BagGrid.TryMask(anchor, ItemFootprint.One, out ulong mask));
        Assert.Equal(0UL, mask);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 1)]
    [InlineData(1, 5)]
    [InlineData(8, 8)]
    [InlineData(255, 255)]
    [InlineData(64, 1)]
    public void AMalformedFootprint_HasNoMask_EvenOnAFreeBoard(int cols, int rows)
    {
        for (int anchor = 0; anchor < 64; anchor++)
        {
            Assert.False(BagGrid.TryMask(anchor, F(cols, rows), out ulong mask));
            Assert.Equal(0UL, mask);
        }
    }

    [Theory]
    [InlineData(7, 2, 1)]
    [InlineData(6, 3, 1)]
    [InlineData(5, 4, 1)]
    [InlineData(61, 4, 1)]
    [InlineData(56, 1, 2)]
    [InlineData(63, 1, 2)]
    [InlineData(48, 1, 3)]
    [InlineData(40, 1, 4)]
    [InlineData(37, 4, 4)]
    public void AnItemThatWouldRunPastAnEdge_IsRefused_NotWrapped(int anchor, int cols, int rows)
    {
        Assert.False(BagGrid.TryMask(anchor, F(cols, rows), out _));
    }

    // ---- FirstFit ----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(4, 4)]
    [InlineData(1, 4)]
    public void OnAnEmptyBoard_TheFirstFitIsTheOrigin(int cols, int rows)
    {
        Assert.Equal(0, BagGrid.FirstFit(0UL, F(cols, rows)));
    }

    [Fact]
    public void OnAFullBoard_NothingFits()
    {
        Assert.Equal(-1, BagGrid.FirstFit(ulong.MaxValue, ItemFootprint.One));
        Assert.Equal(-1, BagGrid.FirstFit(ulong.MaxValue, F(4, 4)));
    }

    [Fact]
    public void TheFirstFit_IsRowMajorLowestAnchor()
    {
        Assert.Equal(1, BagGrid.FirstFit(1UL << 0, ItemFootprint.One));
        Assert.Equal(1, BagGrid.FirstFit(1UL << 0, F(2, 2)));
        Assert.Equal(2, BagGrid.FirstFit(1UL << 1, F(2, 2)));

        // Cells 0 and 8 taken: a 1x2 starts at 1; a 2x2 starts at 1 as well.
        ulong column = (1UL << 0) | (1UL << 8);
        Assert.Equal(1, BagGrid.FirstFit(column, F(1, 2)));
        Assert.Equal(1, BagGrid.FirstFit(column, F(2, 2)));

        // A hole at cell 8 only: a 1x1 takes it before anything on row 1 further right.
        ulong rowZeroFull = 0xFFUL;
        Assert.Equal(8, BagGrid.FirstFit(rowZeroFull, ItemFootprint.One));
        Assert.Equal(8, BagGrid.FirstFit(rowZeroFull, F(4, 4)));
    }

    [Fact]
    public void AFootprintFitsInTheLastFreeCorner_AtTheTopBit()
    {
        ulong freeCorner = 0xF0F0_F0F0_0000_0000UL;
        Assert.Equal(36, BagGrid.FirstFit(~freeCorner, F(4, 4)));
        Assert.Equal(36, BagGrid.FirstFit(~freeCorner, ItemFootprint.One));

        // Take its top-left cell: the 4x4 no longer fits, a 3x4 and a 4x3 still do.
        ulong corner = ~(freeCorner & ~(1UL << 36));
        Assert.Equal(-1, BagGrid.FirstFit(corner, F(4, 4)));
        Assert.Equal(37, BagGrid.FirstFit(corner, F(3, 4)));
        Assert.Equal(44, BagGrid.FirstFit(corner, F(4, 3)));

        // One free cell, the very last one.
        Assert.Equal(63, BagGrid.FirstFit(~(1UL << 63), ItemFootprint.One));
        Assert.Equal(-1, BagGrid.FirstFit(~(1UL << 63), F(2, 1)));
        Assert.Equal(-1, BagGrid.FirstFit(~(1UL << 63), F(1, 2)));
    }

    [Fact]
    public void ARunOfFreeCells_ThatWrapsPastARowEnd_IsNotAFit()
    {
        // Cells 7 and 8 are free: adjacent bit positions, but not horizontally adjacent on the board.
        ulong occupied = ~((1UL << 7) | (1UL << 8));
        Assert.Equal(7, BagGrid.FirstFit(occupied, ItemFootprint.One));
        Assert.Equal(-1, BagGrid.FirstFit(occupied, F(2, 1)));
        Assert.Equal(-1, BagGrid.FirstFit(occupied, F(1, 2)));
    }

    [Fact]
    public void SixtyFourSingles_FillTheBoardInOrder_ThenNothingFits()
    {
        ulong occupied = 0;
        for (int expected = 0; expected < 64; expected++)
        {
            int anchor = BagGrid.FirstFit(occupied, ItemFootprint.One);
            Assert.Equal(expected, anchor);
            Assert.True(BagGrid.TryMask(anchor, ItemFootprint.One, out ulong mask));
            occupied |= mask;
        }

        Assert.Equal(ulong.MaxValue, occupied);
        Assert.Equal(-1, BagGrid.FirstFit(occupied, ItemFootprint.One));
    }

    [Fact]
    public void SixteenTwoByThrees_Place_Eight_AndTheRestHaveNoRoom()
    {
        ulong occupied = 0;
        int placed = 0;
        for (int n = 0; n < 16; n++)
        {
            int anchor = BagGrid.FirstFit(occupied, F(2, 3));
            if (anchor < 0)
            {
                break;
            }

            Assert.True(BagGrid.TryMask(anchor, F(2, 3), out ulong mask));
            Assert.Equal(0UL, occupied & mask);
            occupied |= mask;
            placed++;
        }

        Assert.Equal(8, placed);
        Assert.Equal(48, BitOperations.PopCount(occupied));

        // Two spare rows remain, so smaller things still fit, and a 2x3 does not.
        Assert.Equal(-1, BagGrid.FirstFit(occupied, F(2, 3)));
        Assert.Equal(48, BagGrid.FirstFit(occupied, F(2, 2)));
    }

    [Fact]
    public void AMalformedFootprint_NeverFits()
    {
        Assert.Equal(-1, BagGrid.FirstFit(0UL, F(0, 0)));
        Assert.Equal(-1, BagGrid.FirstFit(0UL, F(0, 1)));
        Assert.Equal(-1, BagGrid.FirstFit(0UL, F(5, 1)));
        Assert.Equal(-1, BagGrid.FirstFit(0UL, F(255, 255)));
    }

    [Fact]
    public void FirstFit_MatchesAnAnchorByAnchorSearch_OnRandomBoards()
    {
        Random random = new(20261101);
        int hits = 0;
        for (int board = 0; board < 3000; board++)
        {
            ulong occupied = 0;
            double density = random.NextDouble();
            for (int cell = 0; cell < 64; cell++)
            {
                if (random.NextDouble() < density * 0.7)
                {
                    occupied |= 1UL << cell;
                }
            }

            for (int cols = 1; cols <= 4; cols++)
            {
                for (int rows = 1; rows <= 4; rows++)
                {
                    int expected = -1;
                    for (int anchor = 0; anchor < 64 && expected < 0; anchor++)
                    {
                        if (BagGrid.TryMask(anchor, F(cols, rows), out ulong mask) && (occupied & mask) == 0)
                        {
                            expected = anchor;
                        }
                    }

                    Assert.Equal(expected, BagGrid.FirstFit(occupied, F(cols, rows)));
                    if (expected >= 0)
                    {
                        hits++;
                    }
                }
            }
        }

        Assert.True(hits > 10_000, $"only {hits} fits were exercised");
    }

    // ---- FootprintOf ----

    [Fact]
    public void FootprintOf_AnUnknownOrMissingKind_IsOneByOne()
    {
        Assert.Equal(ItemFootprint.One, BagGrid.FootprintOf(null));
        Assert.Equal(ItemFootprint.One, BagGrid.FootprintOf(string.Empty));
        Assert.Equal(ItemFootprint.One, BagGrid.FootprintOf("no.such.kind"));
        Assert.Equal(ItemFootprint.One, BagGrid.FootprintOf("retired.kind"));
    }

    [Fact]
    public void FootprintOf_AShippedKind_IsItsDefinitionsFootprint()
    {
        Assert.NotEmpty(ContentCatalog.Shipped.Items);
        foreach (ItemDefinition definition in ContentCatalog.Shipped.Items)
        {
            Assert.Equal(definition.Footprint, BagGrid.FootprintOf(definition.Key));
            Assert.InRange(definition.Footprint.Cols, 1, BagGrid.MaxFootprint);
            Assert.InRange(definition.Footprint.Rows, 1, BagGrid.MaxFootprint);
        }
    }

    // ---- PlanMove: verdicts ----

    [Fact]
    public void DroppingOnTheOwnAnchor_IsSame()
    {
        Board board = new Board().Put(10, 2, 3);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Same, -1, -1), board.Plan(10, 10));
    }

    [Fact]
    public void DroppingOnAFreeCell_IsAMove()
    {
        Board board = new Board().Put(0, 1, 1).Put(20, 2, 3);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Move, -1, -1), board.Plan(0, 63));
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Move, -1, -1), board.Plan(20, 5));
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Move, -1, -1), board.Plan(20, 45));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void AShortShiftInsideTheItemsOwnArea_IsAMove(int to)
    {
        // 2x2 at 0 covers 0,1,8,9. Every target here overlaps only its own old cells (or free ones).
        Board board = new Board().Put(0, 2, 2);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Move, -1, -1), board.Plan(0, to));
    }

    [Fact]
    public void AnItemMovesToTheLastLegalAnchor_AndNoFurther()
    {
        Board board = new Board().Put(0, 4, 4);
        Assert.Equal(BagMoveVerdict.Move, board.Plan(0, 36).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 37).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 44).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 63).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 7).Verdict);
        Assert.Equal(BagMoveVerdict.Move, board.Plan(0, 4).Verdict);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(48)]
    [InlineData(56)]
    [InlineData(63)]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ATwoByThree_DroppedWhereItWouldRunOffTheBoard_IsBlocked(int to)
    {
        Board board = new Board().Put(0, 2, 3);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Blocked, -1, -1), board.Plan(0, to));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(1000)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AFromOffTheBoard_IsBlocked(int from)
    {
        Board board = new Board().Put(0, 1, 1);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(from, 5).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(from, from).Verdict);
    }

    [Fact]
    public void AFromThatIsNotAnItemsAnchor_IsBlocked()
    {
        Board board = new Board().Put(9, 2, 2);

        // An empty cell, and each covered cell that is not the anchor.
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 30).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(10, 30).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(17, 30).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(18, 30).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(10, 10).Verdict);
        Assert.Equal(BagMoveVerdict.Move, board.Plan(9, 30).Verdict);
    }

    // ---- PlanMove: swaps (D10) ----

    [Fact]
    public void TwoSingles_Swap_TheOtherGoesToTheMoversOldAnchor()
    {
        Board board = new Board().Put(3, 1, 1).Put(40, 1, 1);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 40, 3), board.Plan(3, 40));
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 3, 40), board.Plan(40, 3));
    }

    [Fact]
    public void TwoEqualBlocks_Swap_InPlace()
    {
        Board board = new Board().Put(0, 2, 3).Put(4, 2, 3);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 4, 0), board.Plan(0, 4));
    }

    [Fact]
    public void ADropOnAnyCoveredCellOfTheOtherItem_NamesItsAnchor()
    {
        // The mover's top-left lands on a non-anchor cell of the 2x2 at 10 (cells 10,11,18,19).
        foreach (int to in new[] { 11, 18, 19 })
        {
            Board board = new Board().Put(0, 1, 1).Put(10, 2, 2);
            Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 10, 0), board.Plan(0, to));
        }
    }

    [Fact]
    public void ABiggerMover_OverASmallItem_SendsItToTheMoversOldAnchor()
    {
        Board board = new Board().Put(0, 2, 2).Put(18, 1, 1);

        // New area 9,10,17,18 covers the single at 18 only.
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 18, 0), board.Plan(0, 9));
    }

    [Fact]
    public void AShiftOntoTheNeighbour_WhileOverlappingOwnCells_IsASwap()
    {
        // 2x2 at 0 (0,1,8,9) moves to 1 (1,2,9,10): its own cells 1 and 9 do not count, the single at 2 does.
        Board board = new Board().Put(0, 2, 2).Put(2, 1, 1);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 2, 0), board.Plan(0, 1));
    }

    [Fact]
    public void WhenTheOtherDoesNotFitAtTheMoversOldAnchor_ItTakesTheFirstFit()
    {
        // Mover 2x2 at 0, other 2x2 at 2. Mover to 1 covers 1,2,9,10: the other cannot go to 0 (it would
        // sit on the mover's new cells 1 and 9). Its own old cells are free, so 3 is the first fit.
        Board board = new Board().Put(0, 2, 2).Put(2, 2, 2);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 2, 3), board.Plan(0, 1));
    }

    [Fact]
    public void WhenTheMoversOldAnchorIsTaken_TheOtherTakesTheFirstFitElsewhere()
    {
        // Single at 0 drops on a cell of the 2x2 at 10 (cells 10,11,18,19). Another single sits at 1, so the
        // 2x2 cannot go to 0. First fit with the mover on 11: anchors 0..3 are blocked, 4 is free.
        Board board = new Board().Put(0, 1, 1).Put(1, 1, 1).Put(10, 2, 2);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 10, 4), board.Plan(0, 11));
    }

    [Fact]
    public void WhenTheOtherFitsNowhere_TheSwapIsBlocked()
    {
        // Every cell but a 2x2 at 0 holds a single. The single from 63 dropped on cell 0 would displace the
        // 2x2, which fits at neither 63 nor any first fit (only 1, 8, 9 and 63 are free, never a 2x2).
        Board board = new Board().Put(0, 2, 2);
        for (int cell = 0; cell < 64; cell++)
        {
            if (cell is 0 or 1 or 8 or 9)
            {
                continue;
            }

            board.Put(cell, 1, 1);
        }

        Assert.Equal(new BagMovePlan(BagMoveVerdict.Blocked, -1, -1), board.Plan(63, 0));
    }

    [Fact]
    public void TwoOrMoreItemsUnderTheNewArea_IsBlocked()
    {
        Board board = new Board().Put(0, 2, 2).Put(18, 1, 1).Put(19, 1, 1);

        // 2x2 at 9 covers 9,10,17,18: only the single at 18 is under it. At 10 it covers 10,11,18,19: two.
        Assert.Equal(BagMoveVerdict.Swap, board.Plan(0, 9).Verdict);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Blocked, -1, -1), board.Plan(0, 10));
    }

    [Fact]
    public void AMoverCoveringTwoDifferentItems_IsBlocked_EvenWhenBothCouldBeRehomed()
    {
        // The same two singles at 34 and 35: a 1x1 covers one of them (a swap), a 2x1 covers both.
        Board single = new Board().Put(0, 1, 1).Put(34, 1, 1).Put(35, 1, 1);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 34, 0), single.Plan(0, 34));

        Board wide = new Board().Put(0, 2, 1).Put(34, 1, 1).Put(35, 1, 1);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Blocked, -1, -1), wide.Plan(0, 34));
    }

    [Fact]
    public void AnEdgeAnchorSwap_WorksAtTheLastRowAndColumn()
    {
        Board board = new Board().Put(63, 1, 1).Put(0, 1, 1);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 63, 0), board.Plan(0, 63));
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 0, 63), board.Plan(63, 0));

        Board corner = new Board().Put(36, 4, 4).Put(0, 2, 2);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 36, 0), corner.Plan(0, 36));
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 0, 36), corner.Plan(36, 0));
    }

    [Fact]
    public void FourQuadrants_SwapWithEachOther_AndNothingElseIsLegal()
    {
        // Four 4x4 quadrants: any of them swaps with any other in place.
        Board board = new Board().Put(0, 4, 4).Put(4, 4, 4).Put(32, 4, 4).Put(36, 4, 4);
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 36, 0), board.Plan(0, 36));
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Swap, 4, 0), board.Plan(0, 4));

        // Off by one cell it would cover two quadrants.
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 5).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 9).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, board.Plan(0, 28).Verdict);
    }

    // ---- PlanMove: hostile and malformed input ----

    [Fact]
    public void ShortSpans_AreBlocked_NotAnException()
    {
        Board board = new Board().Put(0, 1, 1);
        sbyte[] shortOwners = new sbyte[63];
        Array.Fill(shortOwners, (sbyte)-1);
        shortOwners[0] = 0;
        ItemFootprint[] shortFootprints = new ItemFootprint[63];
        shortFootprints[0] = ItemFootprint.One;

        Assert.Equal(BagMoveVerdict.Blocked, BagGrid.PlanMove(shortOwners, board.Footprints, 0, 5).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, BagGrid.PlanMove(board.Owners, shortFootprints, 0, 5).Verdict);
        Assert.Equal(BagMoveVerdict.Blocked, BagGrid.PlanMove(ReadOnlySpan<sbyte>.Empty, ReadOnlySpan<ItemFootprint>.Empty, 0, 5).Verdict);
    }

    [Fact]
    public void AMoversMalformedFootprint_IsBlocked()
    {
        foreach (ItemFootprint bad in new[] { F(0, 0), F(0, 2), F(2, 0), F(5, 1), F(1, 5), F(255, 255), F(8, 8) })
        {
            Board board = new Board().Put(0, 1, 1);
            board.Footprints[0] = bad;
            Assert.Equal(new BagMovePlan(BagMoveVerdict.Blocked, -1, -1), board.Plan(0, 20));
        }

        // A footprint that runs off the board from where the item sits.
        Board offBoard = new Board().Put(7, 1, 1);
        offBoard.Footprints[7] = F(2, 1);
        Assert.Equal(BagMoveVerdict.Blocked, offBoard.Plan(7, 20).Verdict);
    }

    [Fact]
    public void AnOwnerThatIsNoAnchor_UnderTheNewArea_IsBlocked()
    {
        Board board = new Board().Put(0, 1, 1);
        board.Owners[20] = 100;
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Blocked, -1, -1), board.Plan(0, 20));
        Assert.Equal(BagMoveVerdict.Move, board.Plan(0, 21).Verdict);

        // And it still counts as taken when an item looks for a place to land.
        Board swap = new Board().Put(0, 1, 1).Put(10, 2, 2);
        swap.Owners[8] = 100;
        swap.Owners[1] = 127;
        swap.Owners[9] = 64;
        Assert.Equal(BagMoveVerdict.Swap, swap.Plan(0, 10).Verdict);
        BagMovePlan plan = swap.Plan(0, 10);
        Assert.Equal(10, plan.OtherAnchor);
        Assert.True(BagGrid.TryMask(plan.OtherTo, F(2, 2), out ulong placed));
        Assert.Equal(0UL, placed & ((1UL << 8) | (1UL << 1) | (1UL << 9) | (1UL << 10)));
    }

    [Fact]
    public void AnEmptyMarker_ThatIsAnyNegativeNumber_IsAnEmptyCell()
    {
        Board board = new Board().Put(0, 1, 1);
        board.Owners[20] = sbyte.MinValue;
        board.Owners[21] = -2;
        Assert.Equal(BagMoveVerdict.Move, board.Plan(0, 20).Verdict);
        Assert.Equal(BagMoveVerdict.Move, board.Plan(0, 21).Verdict);
    }

    [Fact]
    public void AnOverlappedItemThatClaimsCellsButIsNoAnchor_IsBlocked()
    {
        // A cell says it belongs to anchor 30, but cell 30 does not say so itself.
        Board board = new Board().Put(0, 1, 1);
        board.Owners[20] = 30;
        Assert.Equal(new BagMovePlan(BagMoveVerdict.Blocked, -1, -1), board.Plan(0, 20));
    }

    [Fact]
    public void PlanMove_DoesNotTouchItsInputs()
    {
        Board board = new Board().Put(0, 2, 2).Put(2, 2, 2).Put(40, 3, 2);
        sbyte[] owners = (sbyte[])board.Owners.Clone();
        ItemFootprint[] footprints = (ItemFootprint[])board.Footprints.Clone();
        for (int from = 0; from < 64; from++)
        {
            for (int to = 0; to < 64; to++)
            {
                _ = board.Plan(from, to);
            }
        }

        Assert.Equal(owners, board.Owners);
        Assert.Equal(footprints, board.Footprints);
    }

    // ---- PlanMove: against a slow reference ----

    private readonly record struct Item(int Anchor, int Cols, int Rows);

    [Fact]
    public void PlanMove_MatchesASlowReference_AndKeepsTheBoardValid_OnRandomBoards()
    {
        Random random = new(7_2026);
        int[] verdicts = new int[4];
        for (int round = 0; round < 4000; round++)
        {
            List<Item> items = RandomItems(random);
            if (items.Count == 0)
            {
                continue;
            }

            Board board = Board.From(items);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Item mover = items[random.Next(items.Count)];
                int to = random.Next(5) == 0 ? mover.Anchor + random.Next(-9, 10) : random.Next(64);
                if (to is < 0 or > 63 && random.Next(2) == 0)
                {
                    to = random.Next(64);
                }

                BagMovePlan actual = board.Plan(mover.Anchor, to);
                BagMovePlan expected = Reference(items, mover.Anchor, to);
                Assert.Equal(expected, actual);
                verdicts[(int)actual.Verdict]++;

                if (actual.Verdict is BagMoveVerdict.Blocked or BagMoveVerdict.Same)
                {
                    continue;
                }

                // Apply the plan the way a caller would and check the board is still legal.
                List<Item> after = new(items);
                int moverIndex = items.FindIndex(i => i.Anchor == mover.Anchor);
                int otherIndex = actual.Verdict == BagMoveVerdict.Swap ? items.FindIndex(i => i.Anchor == actual.OtherAnchor) : -1;
                after[moverIndex] = mover with { Anchor = to };
                if (actual.Verdict == BagMoveVerdict.Swap)
                {
                    Assert.True(otherIndex >= 0 && otherIndex != moverIndex, "swap partner must be another item");
                    Assert.NotEqual(actual.OtherAnchor, actual.OtherTo);
                    after[otherIndex] = after[otherIndex] with { Anchor = actual.OtherTo };
                }

                AssertLegal(after);
                Assert.Equal(items.Count, after.Count);
            }
        }

        Assert.True(verdicts[(int)BagMoveVerdict.Same] > 20, $"Same {verdicts[0]}");
        Assert.True(verdicts[(int)BagMoveVerdict.Move] > 1000, $"Move {verdicts[1]}");
        Assert.True(verdicts[(int)BagMoveVerdict.Swap] > 1000, $"Swap {verdicts[2]}");
        Assert.True(verdicts[(int)BagMoveVerdict.Blocked] > 1000, $"Blocked {verdicts[3]}");
    }

    [Fact]
    public void PlanMove_OnAPackedBoard_MatchesTheReference_ForEveryFromAndTo()
    {
        // Pack the board first-fit with a fixed mix, then ask about every pair of anchors.
        ItemFootprint[] mix = [F(2, 3), F(1, 1), F(2, 2), F(1, 2), F(3, 2), F(1, 1), F(4, 1), F(2, 1), F(1, 3), F(1, 1)];
        List<Item> items = [];
        ulong occupied = 0;
        for (int n = 0; n < 40; n++)
        {
            ItemFootprint f = mix[n % mix.Length];
            int anchor = BagGrid.FirstFit(occupied, f);
            if (anchor < 0)
            {
                continue;
            }

            Assert.True(BagGrid.TryMask(anchor, f, out ulong mask));
            occupied |= mask;
            items.Add(new Item(anchor, f.Cols, f.Rows));
        }

        Assert.True(items.Count >= 12);
        Board board = Board.From(items);
        int swaps = 0;
        for (int from = -1; from <= 64; from++)
        {
            for (int to = -1; to <= 64; to++)
            {
                BagMovePlan expected = Reference(items, from, to);
                Assert.Equal(expected, board.Plan(from, to));
                if (expected.Verdict == BagMoveVerdict.Swap)
                {
                    swaps++;
                }
            }
        }

        Assert.True(swaps > 50, $"only {swaps} swaps were exercised");
    }

    // ---- allocation ----

    [Fact]
    public void TheBoardMath_AllocatesNothing()
    {
        Board board = new Board().Put(0, 2, 3).Put(2, 2, 2).Put(20, 1, 1).Put(36, 4, 4).Put(35, 1, 1);
        long sink = 0;

        // Warm up, so jitting is not counted.
        Round(board, ref sink);
        Round(board, ref sink);
        _ = BagGrid.FootprintOf("no.such.kind");
        _ = BagGrid.FootprintOf(ContentCatalog.Shipped.Items[0].Key);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            Round(board, ref sink);
            sink += BagGrid.FootprintOf(ContentCatalog.Shipped.Items[0].Key).Cols;
            sink += BagGrid.FootprintOf("no.such.kind").Rows;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        Assert.NotEqual(long.MinValue, sink);
    }

    private static void Round(Board board, ref long sink)
    {
        for (int a = 0; a < 64; a++)
        {
            ulong occupied = (ulong)a * 0x9E37_79B9_7F4A_7C15UL;
            sink += BagGrid.FirstFit(occupied, F((a & 3) + 1, ((a >> 2) & 3) + 1));
            _ = BagGrid.TryMask(a, F(2, 3), out ulong mask);
            sink += (long)(mask & 0xFF);
            for (int b = 0; b < 64; b += 7)
            {
                BagMovePlan plan = board.Plan(a, b);
                sink += (int)plan.Verdict + plan.OtherAnchor + plan.OtherTo;
            }
        }
    }

    // ---- helpers ----

    private sealed class Board
    {
        public sbyte[] Owners { get; } = new sbyte[BagGrid.CellCount];

        public ItemFootprint[] Footprints { get; } = new ItemFootprint[BagGrid.CellCount];

        private ulong _occupied;

        public Board()
        {
            Array.Fill(Owners, (sbyte)-1);
        }

        public static Board From(IEnumerable<Item> items)
        {
            Board board = new();
            foreach (Item item in items)
            {
                board.Put(item.Anchor, item.Cols, item.Rows);
            }

            return board;
        }

        public Board Put(int anchor, int cols, int rows)
        {
            ItemFootprint footprint = F(cols, rows);
            Assert.True(BagGrid.TryMask(anchor, footprint, out ulong mask), $"{cols}x{rows} at {anchor} is off the board");
            Assert.Equal(0UL, _occupied & mask);
            for (int cell = 0; cell < BagGrid.CellCount; cell++)
            {
                if ((mask & (1UL << cell)) != 0)
                {
                    Owners[cell] = (sbyte)anchor;
                }
            }

            Footprints[anchor] = footprint;
            _occupied |= mask;
            return this;
        }

        public BagMovePlan Plan(int from, int to) => BagGrid.PlanMove(Owners, Footprints, from, to);
    }

    // Items scattered at random, kept only where they fit and do not overlap.
    private static List<Item> RandomItems(Random random)
    {
        List<Item> items = [];
        bool[,] taken = new bool[8, 8];
        int attempts = random.Next(1, 40);
        for (int n = 0; n < attempts; n++)
        {
            int cols = random.Next(1, 5);
            int rows = random.Next(1, 5);
            if (random.Next(3) != 0)
            {
                cols = random.Next(1, 3);
                rows = random.Next(1, 3);
            }

            int anchor = random.Next(64);
            if (!Fits(taken, anchor, cols, rows))
            {
                continue;
            }

            Mark(taken, anchor, cols, rows, true);
            items.Add(new Item(anchor, cols, rows));
        }

        return items;
    }

    private static bool InBounds(int anchor, int cols, int rows) =>
        anchor is >= 0 and < 64 && (anchor % 8) + cols <= 8 && (anchor / 8) + rows <= 8;

    private static bool Fits(bool[,] taken, int anchor, int cols, int rows)
    {
        if (!InBounds(anchor, cols, rows))
        {
            return false;
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (taken[(anchor / 8) + r, (anchor % 8) + c])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Mark(bool[,] taken, int anchor, int cols, int rows, bool value)
    {
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                taken[(anchor / 8) + r, (anchor % 8) + c] = value;
            }
        }
    }

    private static bool Overlaps(Item a, int aAnchor, Item b)
    {
        int ax = aAnchor % 8;
        int ay = aAnchor / 8;
        int bx = b.Anchor % 8;
        int by = b.Anchor / 8;
        return ax < bx + b.Cols && bx < ax + a.Cols && ay < by + b.Rows && by < ay + a.Rows;
    }

    // The rules of decision D10 written cell by cell, with no bit tricks.
    private static BagMovePlan Reference(List<Item> items, int from, int to)
    {
        BagMovePlan blocked = new(BagMoveVerdict.Blocked, -1, -1);
        int moverIndex = items.FindIndex(i => i.Anchor == from);
        if (from is < 0 or > 63 || to is < 0 or > 63 || moverIndex < 0)
        {
            return blocked;
        }

        Item mover = items[moverIndex];
        if (from == to)
        {
            return new BagMovePlan(BagMoveVerdict.Same, -1, -1);
        }

        if (!InBounds(to, mover.Cols, mover.Rows))
        {
            return blocked;
        }

        List<Item> under = items.Where(i => i.Anchor != from && Overlaps(mover, to, i)).ToList();
        if (under.Count == 0)
        {
            return new BagMovePlan(BagMoveVerdict.Move, -1, -1);
        }

        if (under.Count > 1)
        {
            return blocked;
        }

        Item other = under[0];
        bool[,] taken = new bool[8, 8];
        foreach (Item item in items)
        {
            if (item.Anchor != from && item.Anchor != other.Anchor)
            {
                Mark(taken, item.Anchor, item.Cols, item.Rows, true);
            }
        }

        Mark(taken, to, mover.Cols, mover.Rows, true);
        if (Fits(taken, from, other.Cols, other.Rows))
        {
            return new BagMovePlan(BagMoveVerdict.Swap, other.Anchor, from);
        }

        for (int anchor = 0; anchor < 64; anchor++)
        {
            if (Fits(taken, anchor, other.Cols, other.Rows))
            {
                return new BagMovePlan(BagMoveVerdict.Swap, other.Anchor, anchor);
            }
        }

        return blocked;
    }

    private static void AssertLegal(List<Item> items)
    {
        bool[,] taken = new bool[8, 8];
        foreach (Item item in items)
        {
            Assert.True(Fits(taken, item.Anchor, item.Cols, item.Rows), $"{item} overlaps or leaves the board");
            Mark(taken, item.Anchor, item.Cols, item.Rows, true);
        }
    }
}
