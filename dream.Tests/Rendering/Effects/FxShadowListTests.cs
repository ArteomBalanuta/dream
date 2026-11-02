using Dream.Rendering.Effects;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

public class FxShadowListTests
{
    private static readonly Vector3 Camera = new(0f, 5f, 0f);
    private static readonly Vector3 Centre = new(5f, 1f, 5f);

    [Fact]
    public void A_Grounded_Blob_Lies_Flat_Just_Above_The_Ground_At_Seventy_Percent_Alpha()
    {
        FxShadowList shadows = new();
        shadows.Add(new Vector3(5f, 2f, 6f), 2f, FxShadowList.PlayerRadius);
        FxInstance[] destination = new FxInstance[4];

        int written = shadows.Write(destination, Camera, Centre, 1f);

        Assert.Equal(1, written);
        FxInstance blob = destination[0];
        Assert.Equal(new Vector3(5f, 2f + FxShadowList.Lift, 6f), blob.Position);
        Assert.Equal((float)FxFrame.Blob, blob.Frame);
        Assert.Equal(FxInstance.GroundAligned, blob.Stretch);
        Assert.Equal(FxShadowList.PlayerRadius, blob.HalfSize, 4);
        Assert.Equal(FxInstance.PackColor(0f, 0f, 0f, FxShadowList.NightAlpha), blob.Color);
        Assert.Equal(0u, blob.Color & 0x00FFFFFFu);
    }

    [Fact]
    public void The_Blob_Is_Stronger_By_Day_And_Blends_Linearly_With_The_Night_Factor()
    {
        FxShadowList shadows = new();
        shadows.Add(new Vector3(5f, 2f, 6f), 2f, 0.5f);
        FxInstance[] destination = new FxInstance[1];

        shadows.Write(destination, Camera, Centre, 0f);
        Assert.Equal(FxInstance.PackColor(0f, 0f, 0f, 0.95f), destination[0].Color);
        shadows.Write(destination, Camera, Centre, 0.5f);
        Assert.Equal(FxInstance.PackColor(0f, 0f, 0f, 0.825f), destination[0].Color);
        shadows.Write(destination, Camera, Centre, 1f);
        Assert.Equal(FxInstance.PackColor(0f, 0f, 0f, 0.7f), destination[0].Color);
    }

    [Fact]
    public void Begin_Culls_Far_Blobs_In_Add_So_They_Cannot_Take_A_Near_Slot()
    {
        FxShadowList shadows = new();
        shadows.Begin(Centre);
        for (int i = 0; i < FxLimits.ShadowBudget + 4; i++)
        {
            shadows.Add(new Vector3(100f + i, 1f, 5f), 1f, 0.5f);
        }

        Assert.Equal(0, shadows.Count);
        shadows.Add(new Vector3(6f, 1f, 5f), 1f, 0.5f);
        Assert.Equal(1, shadows.Count);
        FxInstance[] destination = new FxInstance[4];
        Assert.Equal(1, shadows.Write(destination, Camera, Centre, 1f));
        Assert.Equal(6f, destination[0].Position.X);
    }

    [Fact]
    public void A_Blob_Under_A_Jump_Fades_And_Widens()
    {
        FxShadowList shadows = new();
        shadows.Add(new Vector3(5f, 2f + FxShadowList.MaxAirHeight, 6f), 2f, 0.5f);
        FxInstance[] destination = new FxInstance[1];

        shadows.Write(destination, Camera, Centre, 1f);

        Assert.Equal(0.5f * 1.3f, destination[0].HalfSize, 4);
        Assert.Equal(FxInstance.PackColor(0f, 0f, 0f, FxShadowList.NightAlpha * 0.35f), destination[0].Color);
    }

    [Fact]
    public void Blobs_Beyond_Thirty_Metres_Or_Within_The_Camera_Are_Skipped()
    {
        FxShadowList shadows = new();
        shadows.Add(new Vector3(36f, 1f, 5f), 1f, 0.5f);
        shadows.Add(new Vector3(0f, 0f, 1f), 0f, 0.5f);
        shadows.Add(new Vector3(3f, 0f, 3f), 0f, 0.5f);
        FxInstance[] destination = new FxInstance[4];

        Assert.Equal(1, shadows.Write(destination, Vector3.Zero, Centre, 1f));
        Assert.Equal(3f, destination[0].Position.X);
    }

    [Fact]
    public void Add_Ignores_Past_The_Budget_And_Write_Never_Passes_The_Destination()
    {
        FxShadowList shadows = new();
        for (int i = 0; i < FxLimits.ShadowBudget + 10; i++)
        {
            shadows.Add(new Vector3(5f, 1f, 5f + i * 0.1f), 1f, 0.5f);
        }

        Assert.Equal(FxLimits.ShadowBudget, shadows.Count);
        FxInstance[] small = new FxInstance[3];
        Assert.Equal(3, shadows.Write(small, Camera, Centre, 1f));
        FxInstance[] large = new FxInstance[FxLimits.ShadowBudget + 8];
        Assert.Equal(FxLimits.ShadowBudget, shadows.Write(large, Camera, Centre, 1f));

        shadows.Clear();
        Assert.Equal(0, shadows.Write(large, Camera, Centre, 1f));
    }

    [Fact]
    public void Add_And_Write_Allocate_Nothing()
    {
        FxShadowList shadows = new();
        FxInstance[] destination = new FxInstance[FxLimits.MaxInstances];

        AllocationAssert.AtMost(0, () =>
        {
            shadows.Clear();
            for (int i = 0; i < 8; i++)
            {
                shadows.Add(new Vector3(5f + i, 1.5f, 6f), 1f, 0.5f);
            }

            shadows.Write(destination, Camera, Centre, 1f);
        });
    }

    [Fact]
    public void The_Probe_Finds_The_Top_Of_The_Ground_Under_The_Feet()
    {
        Map map = new(physics: null, generateStartupTerrain: true, width: 10, depth: 10, headless: true);
        Assert.True(FindColumn(map, out int x, out int z, out int top));

        Vector3 standing = new(x + 0.5f, top + 1f, z + 0.5f);
        Assert.True(FxGroundProbe.TryFindGroundY(map, standing, out float groundY));
        Assert.Equal(top + 1f, groundY);

        Vector3 jumping = standing + new Vector3(0f, 2.5f, 0f);
        Assert.True(FxGroundProbe.TryFindGroundY(map, jumping, out groundY));
        Assert.Equal(top + 1f, groundY);

        Vector3 farAbove = standing + new Vector3(0f, FxGroundProbe.MaxCellsDown + 3f, 0f);
        Assert.False(FxGroundProbe.TryFindGroundY(map, farAbove, out _));
        Assert.False(FxGroundProbe.TryFindGroundY(map, new Vector3(500f, 1f, 500f), out _));
        Assert.False(FxGroundProbe.TryFindGroundY(map, new Vector3(float.NaN, 1f, 1f), out _));
    }

    [Fact]
    public void The_Probe_Reads_The_Top_Solid_Child_Of_A_Subdivided_Cell_Not_A_Full_Cube()
    {
        Map map = new(physics: null, generateStartupTerrain: true, width: 10, depth: 10, headless: true);
        Assert.True(FindColumn(map, out int x, out int z, out int top));
        GeometrySubdivisionState state = map.TryGetGeometryCellAtCell(x, top, z)!.EnsureSubdivisionState();
        int resolution = state.Resolution;
        Assert.True(resolution >= 2);

        // Sculpture-style: only the lowest layer of children is solid.
        state.SetAllChildrenEmpty();
        for (int cx = 0; cx < resolution; cx++)
        {
            for (int cz = 0; cz < resolution; cz++)
            {
                state.SetChildSolid(new GeometrySubcellKey(cx, 0, cz), true);
            }
        }

        Vector3 standing = new(x + 0.5f, top + 1f / resolution, z + 0.5f);
        Assert.True(FxGroundProbe.TryFindGroundY(map, standing, out float groundY));
        Assert.Equal(top + 1f / resolution, groundY, 1e-4f);

        // A child top above the feet is skipped: mid-cell, the top of the lowest layer is not
        // reached by feet half a child up, so the scan falls to the cell below (its full top is y).
        Vector3 inside = new(x + 0.5f, top + 0.5f / resolution, z + 0.5f);
        bool found = FxGroundProbe.TryFindGroundY(map, inside, out groundY);
        Assert.Equal(CellBelowTop(map, x, top, z, out float below), found);
        if (found)
        {
            Assert.Equal(below, groundY, 1e-4f);
        }

        // A solid child below an above-feet child: the lower child's top is the answer.
        state.SetChildSolid(new GeometrySubcellKey(resolution / 2, 1, resolution / 2), true);
        state.SetChildSolid(new GeometrySubcellKey(resolution / 2, 3 % resolution, resolution / 2), true);
        Vector3 between = new(x + (resolution / 2 + 0.5f) / resolution, top + 2.5f / resolution, z + (resolution / 2 + 0.5f) / resolution);
        Assert.True(FxGroundProbe.TryFindGroundY(map, between, out groundY));
        Assert.Equal(top + 2f / resolution, groundY, 1e-4f);

        // An empty cell falls through to the cell below, not the full-cube top.
        state.SetAllChildrenEmpty();
        Vector3 above = new(x + 0.5f, top + 1f, z + 0.5f);
        found = FxGroundProbe.TryFindGroundY(map, above, out groundY);
        Assert.Equal(CellBelowTop(map, x, top, z, out below), found);
        if (found)
        {
            Assert.Equal(below, groundY, 1e-4f);
        }
    }

    // The probe's expected answer under a cell whose column below is a stack of full cubes (or nothing).
    private static bool CellBelowTop(Map map, int x, int top, int z, out float expected)
    {
        expected = 0f;
        for (int y = top - 1; y >= top - FxGroundProbe.MaxCellsDown && y >= 0; y--)
        {
            GeometryCellRecord? record = map.TryGetGeometryCellAtCell(x, y, z);
            if (record != null && record.SubdivisionState == null && record.Definition.ShapeKind == GeometryShapeKind.SolidCube)
            {
                expected = y + 1;
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Feet_Mid_Air_Beside_A_Full_Cube_Wall_Ignore_The_Wall_Top()
    {
        Map map = new(physics: null, generateStartupTerrain: true, width: 10, depth: 10, headless: true);
        Assert.True(FindColumn(map, out int x, out int z, out int top));
        BuildWall(map, x + 1, z, top, subdividedLayer: -1);

        // Jumping 1.5 m up, 0.3 m from the wall: the wall top (top + 3) is above the feet, which are
        // inside the wall's top cell.
        Vector3 feet = new(x + 0.7f, top + 2.5f, z + 0.5f);
        Assert.True(FxGroundProbe.TryFindGroundY(map, feet, FxShadowList.PlayerRadius, out float groundY));
        Assert.Equal(top + 1f, groundY);
    }

    [Fact]
    public void Feet_Mid_Air_Beside_A_Subdivided_Wall_Ignore_The_Wall_Top()
    {
        Map map = new(physics: null, generateStartupTerrain: true, width: 10, depth: 10, headless: true);
        Assert.True(FindColumn(map, out int x, out int z, out int top));
        BuildWall(map, x + 1, z, top, subdividedLayer: 2);

        Vector3 feet = new(x + 0.7f, top + 2.5f, z + 0.5f);
        Assert.True(FxGroundProbe.TryFindGroundY(map, feet, FxShadowList.PlayerRadius, out float groundY));
        Assert.Equal(top + 1f, groundY);

        // Standing on top of the wall, the wall top is the ground.
        Vector3 onWall = new(x + 1.5f, top + 3.4f, z + 0.5f);
        Assert.True(FxGroundProbe.TryFindGroundY(map, onWall, FxShadowList.PlayerRadius, out groundY));
        Assert.Equal(top + 3f, groundY);
    }

    // A wall of cubes in column (x, z) up to a top at top + 3 (the neighbouring column is the
    // floor); the cell at top + subdividedLayer (when >= 0) is made of fully solid children.
    private static void BuildWall(Map map, int x, int z, int top, int subdividedLayer)
    {
        for (int y = 1; y <= top + 2; y++)
        {
            map.AddCube(new Vector3(x, y, z), isStatic: true, withPhysics: false);
        }

        if (subdividedLayer < 0)
        {
            return;
        }

        GeometrySubdivisionState state = map.TryGetGeometryCellAtCell(x, top + subdividedLayer, z)!.EnsureSubdivisionState();
        for (int cx = 0; cx < state.Resolution; cx++)
        {
            for (int cy = 0; cy < state.Resolution; cy++)
            {
                for (int cz = 0; cz < state.Resolution; cz++)
                {
                    state.SetChildSolid(new GeometrySubcellKey(cx, cy, cz), true);
                }
            }
        }
    }


    [Fact]
    public void The_Probe_Finds_The_Higher_Neighbour_At_A_Ledge()
    {
        Map map = new(physics: null, generateStartupTerrain: true, width: 10, depth: 10, headless: true);
        Assert.True(FindColumn(map, out int x, out int z, out int top));
        // Empty the whole column next door, so the ground there is gone (a drop past the margin).
        for (int y = 0; y <= top + FxGroundProbe.MaxCellsDown; y++)
        {
            map.TryGetGeometryCellAtCell(x + 1, y, z)?.EnsureSubdivisionState().SetAllChildrenEmpty();
        }

        Vector3 feet = new(x + 1.1f, top + 1f, z + 0.5f);
        Assert.False(FxGroundProbe.TryFindGroundY(map, feet, out _));
        Assert.True(FxGroundProbe.TryFindGroundY(map, feet, FxShadowList.PlayerRadius, out float groundY));
        Assert.Equal(top + 1f, groundY);

        // Well clear of the edge, the neighbour is outside the capsule and the blob stays unplaced.
        Assert.False(FxGroundProbe.TryFindGroundY(map, new Vector3(x + 1.7f, top + 1f, z + 0.5f), 0.2f, out _));
    }

    [Fact]
    public void The_Scene_Rules_Put_Player_Blobs_At_The_Feet_Skip_The_Dead_And_Scale_Enemies()
    {
        Vector3 feet = FxShadowRules.PlayerFeet(new Vector3(3f, 2.5f, 4f));
        Assert.Equal(new Vector3(3f, 2f, 4f), feet);
        Assert.True(FxShadowRules.RemotePlayerCastsShadow(isAlive: true));
        Assert.False(FxShadowRules.RemotePlayerCastsShadow(isAlive: false));
        Assert.Equal(0.4f * FxShadowList.EnemyRadiusScale, FxShadowRules.EnemyRadius(0.4f, 1f), 1e-5f);
        Assert.Equal(0.4f * FxShadowList.EnemyRadiusScale * 1.5f, FxShadowRules.EnemyRadius(0.4f, 1.5f), 1e-5f);
    }

    [Fact]
    public void The_Probe_Allocates_Nothing()
    {
        Map map = new(physics: null, generateStartupTerrain: true, width: 10, depth: 10, headless: true);
        Assert.True(FindColumn(map, out int x, out int z, out int top));
        Vector3 feet = new(x + 0.5f, top + 3f, z + 0.5f);
        Vector3 away = new(500f, 1f, 500f);

        AllocationAssert.AtMost(0, () =>
        {
            FxGroundProbe.TryFindGroundY(map, feet, out _);
            FxGroundProbe.TryFindGroundY(map, away, out _);
        });
    }

    private static bool FindColumn(Map map, out int x, out int z, out int top)
    {
        for (x = 0; x < 10; x++)
        {
            for (z = 0; z < 10; z++)
            {
                for (top = 12; top >= 0; top--)
                {
                    if (map.TryGetGeometryCellAtCell(x, top, z) != null)
                    {
                        return true;
                    }
                }
            }
        }

        x = z = top = 0;
        return false;
    }
}
