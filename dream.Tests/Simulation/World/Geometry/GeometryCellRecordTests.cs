using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Simulation.World.Geometry;

public class GeometryCellRecordTests
{
    [Fact]
    public void Wedge_Cell_Uses_Preset_Occupancy_Without_Subdivision_Overrides()
    {
        GeometryCellDefinition definition = new GeometryCellDefinition(
            GeometryShapeKind.Wedge,
            GeometryMaterialId.Grass,
            SubdivisionResolution: 4,
            IsSolid: true,
            IsFloor: true);
        GeometryCellRecord record = new GeometryCellRecord(new GeometryCellKey(0, 0, 0), definition);

        Assert.True(record.IsChildSolid(new GeometrySubcellKey(0, 0, 0)));
        Assert.True(record.IsChildSolid(new GeometrySubcellKey(3, 0, 0)));
        Assert.False(record.IsChildSolid(new GeometrySubcellKey(0, 3, 0)));
    }

    [Fact]
    public void Subdivision_Overrides_Can_Replace_Default_Shape_Occupancy()
    {
        GeometryCellDefinition definition = new GeometryCellDefinition(
            GeometryShapeKind.Wedge,
            GeometryMaterialId.Grass,
            SubdivisionResolution: 4,
            IsSolid: true,
            IsFloor: true);
        GeometryCellRecord record = new GeometryCellRecord(new GeometryCellKey(0, 0, 0), definition);

        record.EnsureSubdivisionState().SetChildSolid(new GeometrySubcellKey(0, 0, 0), isSolid: false);

        Assert.False(record.IsChildSolid(new GeometrySubcellKey(0, 0, 0)));
    }
}
