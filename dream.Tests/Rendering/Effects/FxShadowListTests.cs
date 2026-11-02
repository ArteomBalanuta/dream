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

        int written = shadows.Write(destination, Camera, Centre);

        Assert.Equal(1, written);
        FxInstance blob = destination[0];
        Assert.Equal(new Vector3(5f, 2f + FxShadowList.Lift, 6f), blob.Position);
        Assert.Equal((float)FxFrame.Blob, blob.Frame);
        Assert.Equal(FxInstance.GroundAligned, blob.Stretch);
        Assert.Equal(FxShadowList.PlayerRadius, blob.HalfSize, 4);
        Assert.Equal(FxInstance.PackColor(0f, 0f, 0f, FxShadowList.Alpha), blob.Color);
        Assert.Equal(0u, blob.Color & 0x00FFFFFFu);
    }

    [Fact]
    public void A_Blob_Under_A_Jump_Fades_And_Widens()
    {
        FxShadowList shadows = new();
        shadows.Add(new Vector3(5f, 2f + FxShadowList.MaxAirHeight, 6f), 2f, 0.5f);
        FxInstance[] destination = new FxInstance[1];

        shadows.Write(destination, Camera, Centre);

        Assert.Equal(0.5f * 1.3f, destination[0].HalfSize, 4);
        Assert.Equal(FxInstance.PackColor(0f, 0f, 0f, FxShadowList.Alpha * 0.35f), destination[0].Color);
    }

    [Fact]
    public void Blobs_Beyond_Thirty_Metres_Or_Within_The_Camera_Are_Skipped()
    {
        FxShadowList shadows = new();
        shadows.Add(new Vector3(36f, 1f, 5f), 1f, 0.5f);
        shadows.Add(new Vector3(0f, 0f, 1f), 0f, 0.5f);
        shadows.Add(new Vector3(3f, 0f, 3f), 0f, 0.5f);
        FxInstance[] destination = new FxInstance[4];

        Assert.Equal(1, shadows.Write(destination, Vector3.Zero, Centre));
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
        Assert.Equal(3, shadows.Write(small, Camera, Centre));
        FxInstance[] large = new FxInstance[FxLimits.ShadowBudget + 8];
        Assert.Equal(FxLimits.ShadowBudget, shadows.Write(large, Camera, Centre));

        shadows.Clear();
        Assert.Equal(0, shadows.Write(large, Camera, Centre));
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

            shadows.Write(destination, Camera, Centre);
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

        // A child top above the feet is skipped, and the result is never above the feet.
        Vector3 inside = new(x + 0.5f, top + 0.5f / resolution, z + 0.5f);
        FxGroundProbe.TryFindGroundY(map, inside, out groundY);
        Assert.True(groundY <= inside.Y, $"{groundY}");

        // An empty column in the cell falls through to the cell below, not the full-cube top.
        state.SetAllChildrenEmpty();
        Vector3 above = new(x + 0.5f, top + 1f, z + 0.5f);
        FxGroundProbe.TryFindGroundY(map, above, out groundY);
        Assert.True(groundY <= top + 0.001f, $"{groundY}");
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
