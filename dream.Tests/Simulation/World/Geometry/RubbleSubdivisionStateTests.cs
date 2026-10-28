using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.World.Geometry;

// A rubble cell keeps a GeometrySubdivisionState (its presence marks the cell as no longer an
// intact solid cube), but marks every child empty with a flag instead of storing one override per
// child. Before the change SetRubble stored 1000 overrides per cell: about 107 KB allocated per
// fracture and 30 KB retained per rubble cell.
public class RubbleSubdivisionStateTests
{
    // Old per-fracture cost was ~107 KB for the 1000-entry dictionary alone; the whole
    // fracture-and-remove cycle without physics now stays well below one such dictionary.
    private const long FractureCycleBudgetBytes = 16 * 1024;

    private readonly ITestOutputHelper _output;

    public RubbleSubdivisionStateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Fractured_Cell_Has_AllEmpty_Subdivision_State_Without_Per_Child_Overrides()
    {
        Map map = new(physics: null, width: 10, depth: 10);
        GeometryCellKey cell = new(4, 1, 4);
        Assert.True(map.AddCube(new Vector3(cell.X, cell.Y, cell.Z), withPhysics: false));

        Assert.True(map.FractureCubeIntoPieces(cell));

        GeometryCellRecord rubble = Assert.IsType<GeometryCellRecord>(map.TryGetRubbleAtCell(cell));
        GeometrySubdivisionState state = Assert.IsType<GeometrySubdivisionState>(rubble.SubdivisionState);
        int resolution = rubble.Definition.SubdivisionResolution;
        Assert.Equal(resolution, state.Resolution);
        Assert.True(state.AllChildrenEmpty);
        Assert.True(state.HasOverrides);
        Assert.Equal(resolution * resolution * resolution, state.OverrideCount);
        AssertEveryChildEmpty(rubble);
    }

    [Fact]
    public void Restored_Rubble_Has_AllEmpty_Subdivision_State()
    {
        Map map = new(physics: null, width: 10, depth: 10);
        GeometryCellKey cell = new(3, 1, 3);

        Assert.True(map.RestoreFracturePieces(cell, GeometryMaterialId.Stone, CubeFracturePattern.CreateFirstPassRubble(cell), withPhysics: false));

        GeometryCellRecord rubble = Assert.IsType<GeometryCellRecord>(map.TryGetRubbleAtCell(cell));
        Assert.True(Assert.IsType<GeometrySubdivisionState>(rubble.SubdivisionState).AllChildrenEmpty);
        AssertEveryChildEmpty(rubble);
    }

    [Fact]
    public void AllEmpty_Rubble_Matches_Per_Child_Override_Rubble_For_Every_Child()
    {
        GeometryCellRecord flagged = CreateCell();
        flagged.EnsureSubdivisionState().SetAllChildrenEmpty();
        GeometryCellRecord legacy = CreateCell();
        GeometrySubdivisionState legacyState = legacy.EnsureSubdivisionState();
        int resolution = legacy.Definition.SubdivisionResolution;
        ForEachChild(resolution, key => legacyState.SetChildSolid(key, isSolid: false));

        Assert.Equal(legacyState.OverrideCount, flagged.SubdivisionState!.OverrideCount);
        Assert.Equal(legacyState.HasOverrides, flagged.SubdivisionState.HasOverrides);
        ForEachChild(resolution, key =>
        {
            Assert.Equal(legacy.IsChildSolid(key), flagged.IsChildSolid(key));
            Assert.Equal(
                legacyState.TryGetChildSolid(key, out bool legacySolid),
                flagged.SubdivisionState.TryGetChildSolid(key, out bool flaggedSolid));
            Assert.Equal(legacySolid, flaggedSolid);
        });
    }

    [Fact]
    public void Fracture_Cycle_Allocates_Far_Less_Than_Per_Child_Overrides()
    {
        Map map = new(physics: null, width: 10, depth: 10);
        GeometryCellKey cell = new(5, 1, 5);
        IReadOnlyList<GeometryFracturePiece> pieces = CubeFracturePattern.CreateFirstPassRubble(cell);

        long bytes = AllocationAssert.AtMost(FractureCycleBudgetBytes, () =>
        {
            Assert.True(map.RestoreFracturePieces(cell, GeometryMaterialId.Stone, pieces, withPhysics: false));
            Assert.True(map.RemoveRubble(cell));
        });
        _output.WriteLine($"Restore + remove rubble, map without physics: {bytes} B per cycle");
    }

    private static GeometryCellRecord CreateCell()
    {
        return new WorldGenerator().CreateGeometryCell(new Vector3(2f, 1f, 2f), isFloor: false, GeometryMaterialId.Stone);
    }

    private static void AssertEveryChildEmpty(GeometryCellRecord record)
    {
        ForEachChild(record.Definition.SubdivisionResolution, key => Assert.False(record.IsChildSolid(key)));
    }

    private static void ForEachChild(int resolution, Action<GeometrySubcellKey> action)
    {
        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    action(new GeometrySubcellKey(x, y, z));
                }
            }
        }
    }
}
