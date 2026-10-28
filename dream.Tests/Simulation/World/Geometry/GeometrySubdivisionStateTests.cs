using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Simulation.World.Geometry;

public class GeometrySubdivisionStateTests
{
    [Fact]
    public void New_State_Does_Not_Allocate_Child_Overrides()
    {
        GeometrySubdivisionState state = new GeometrySubdivisionState(10);

        Assert.Equal(10, state.Resolution);
        Assert.False(state.HasOverrides);
        Assert.Equal(0, state.OverrideCount);
        Assert.False(state.TryGetChildSolid(new GeometrySubcellKey(0, 0, 0), out _));
    }

    [Fact]
    public void SetChildSolid_Creates_Sparse_Override_State()
    {
        GeometrySubdivisionState state = new GeometrySubdivisionState(10);
        GeometrySubcellKey child = new GeometrySubcellKey(2, 3, 4);

        state.SetChildSolid(child, isSolid: false);

        Assert.True(state.HasOverrides);
        Assert.Equal(1, state.OverrideCount);
        Assert.True(state.TryGetChildSolid(child, out bool isSolid));
        Assert.False(isSolid);
    }

    [Fact]
    public void SetAllChildrenEmpty_Reports_Every_InRange_Child_Empty_Without_Overrides()
    {
        GeometrySubdivisionState state = new GeometrySubdivisionState(4);

        state.SetAllChildrenEmpty();

        Assert.True(state.AllChildrenEmpty);
        Assert.True(state.HasOverrides);
        Assert.Equal(64, state.OverrideCount);
        Assert.True(state.TryGetChildSolid(new GeometrySubcellKey(0, 0, 0), out bool first));
        Assert.False(first);
        Assert.True(state.TryGetChildSolid(new GeometrySubcellKey(3, 3, 3), out bool last));
        Assert.False(last);
        Assert.False(state.TryGetChildSolid(new GeometrySubcellKey(4, 0, 0), out _));
        Assert.False(state.TryGetChildSolid(new GeometrySubcellKey(0, -1, 0), out _));
    }

    [Fact]
    public void SetAllChildrenEmpty_Matches_Setting_Every_Child_Empty_Over_Existing_Overrides()
    {
        GeometrySubcellKey inRange = new GeometrySubcellKey(1, 2, 3);
        GeometrySubcellKey outOfRange = new GeometrySubcellKey(9, 0, 0);
        GeometrySubdivisionState flagged = new GeometrySubdivisionState(4);
        flagged.SetChildSolid(inRange, isSolid: true);
        flagged.SetChildSolid(outOfRange, isSolid: true);

        flagged.SetAllChildrenEmpty();

        Assert.True(flagged.TryGetChildSolid(inRange, out bool inRangeSolid));
        Assert.False(inRangeSolid);
        Assert.True(flagged.TryGetChildSolid(outOfRange, out bool outOfRangeSolid));
        Assert.True(outOfRangeSolid);
        Assert.Equal(65, flagged.OverrideCount);
    }

    [Fact]
    public void Explicit_Override_After_SetAllChildrenEmpty_Wins()
    {
        GeometrySubdivisionState state = new GeometrySubdivisionState(4);
        GeometrySubcellKey child = new GeometrySubcellKey(2, 2, 2);
        state.SetAllChildrenEmpty();

        state.SetChildSolid(child, isSolid: true);

        Assert.True(state.TryGetChildSolid(child, out bool isSolid));
        Assert.True(isSolid);
        Assert.Equal(64, state.OverrideCount);
    }
}
