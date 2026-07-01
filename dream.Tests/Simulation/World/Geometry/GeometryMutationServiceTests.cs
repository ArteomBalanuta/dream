using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Simulation.World.Geometry;

public class GeometryMutationServiceTests
{
    [Fact]
    public void SetChildSolid_Activates_Subdivision_And_Reports_Dirty_Cell()
    {
        GeometryCellDefinition definition = new GeometryCellDefinition(
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Grass,
            SubdivisionResolution: 10,
            IsSolid: true,
            IsFloor: false);
        GeometryCellRecord record = new GeometryCellRecord(new GeometryCellKey(2, 1, 2), definition);
        GeometryMutationService service = new GeometryMutationService();

        GeometryMutationResult result = service.SetChildSolid(
            record,
            new GeometrySubcellKey(1, 1, 1),
            isSolid: false);

        Assert.True(result.SubdivisionActivated);
        Assert.Contains(record.Key, result.ChangedCells);
        Assert.Contains(record.Key, result.RenderDirtyCells);
        Assert.Contains(record.Key, result.CollisionDirtyCells);
        Assert.False(record.IsChildSolid(new GeometrySubcellKey(1, 1, 1)));
    }
}
