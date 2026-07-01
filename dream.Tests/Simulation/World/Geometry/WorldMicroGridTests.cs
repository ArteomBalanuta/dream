using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.World.Geometry;

public class WorldMicroGridTests
{
    [Fact]
    public void ToMicroCellKey_Uses_Global_Absolute_Coordinates()
    {
        WorldMicroCellKey key = WorldMicroGrid.ToMicroCellKey(new Vector3(2.125f, 1.5f, 3.9375f));

        Assert.Equal(new WorldMicroCellKey(34, 24, 63), key);
    }

    [Fact]
    public void ToParentAndLocalKeys_Project_GlobalMicrocell_Into_Current_CellStorage()
    {
        WorldMicroCellKey microcell = new(31, 16, 47);

        GeometryCellKey parent = WorldMicroGrid.ToParentCellKey(microcell);
        GeometrySubcellKey local = WorldMicroGrid.ToLocalSubcellKey(microcell);
        WorldMicroCellKey roundTrip = WorldMicroGrid.ToMicroCellKey(parent, local);

        Assert.Equal(new GeometryCellKey(1, 1, 2), parent);
        Assert.Equal(new GeometrySubcellKey(15, 0, 15), local);
        Assert.Equal(microcell, roundTrip);
    }

    [Fact]
    public void ToWorldCenter_Returns_Center_Of_GlobalMicrocell()
    {
        Vector3 center = WorldMicroGrid.ToWorldCenter(new WorldMicroCellKey(32, 24, 48));

        Assert.Equal(2.03125f, center.X, precision: 5);
        Assert.Equal(1.53125f, center.Y, precision: 5);
        Assert.Equal(3.03125f, center.Z, precision: 5);
    }
}
