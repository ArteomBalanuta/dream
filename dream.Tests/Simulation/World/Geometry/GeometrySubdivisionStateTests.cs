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
}
