using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Ground;
using Dream.Rendering.Props;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ground;

// The scatter renderer submits only what can be seen, per layer, and its index follows the prop layout's rebuilds.
public class GroundScatterRendererCullingTests
{
    private static ViewFrustum IsoFrustum(Vector3 focus)
    {
        Camera camera = new(Vector3.Zero);
        IsometricView.ResolveInto(focus, camera);
        return ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), IsometricView.CreateProjection(1440f / 884f));
    }

    private static GroundAsset Asset(GroundLayer layer, float radius = 0.5f, float height = 1f) =>
        new("t.a", "t.glb", layer == GroundLayer.Decal ? GroundAssetClass.DecalDirt : GroundAssetClass.Grass, layer, radius, 0.2f, height, [GeometryMaterialId.Grass], 1f, 1f, 1f);

    [Theory]
    [InlineData(GroundLayer.Decal)]
    [InlineData(GroundLayer.Cover)]
    [InlineData(GroundLayer.Accent)]
    [InlineData(GroundLayer.Landmark)]
    public void AnAsset_AtTheFocus_IsVisible(GroundLayer layer)
    {
        Vector3 focus = new(50f, 1.5f, 50f);

        Assert.True(GroundScatterRenderer.IsVisible(new GroundPlacement(0, new Vector3(50f, 1f, 50f), 0f, 1f), Asset(layer), focus, IsoFrustum(focus)));
    }

    [Fact]
    public void TheDrawDistance_GrowsWithTheLayer()
    {
        Assert.True(GroundScatterRenderer.DrawDistance(GroundLayer.Decal) <= GroundScatterRenderer.DrawDistance(GroundLayer.Accent));
        Assert.True(GroundScatterRenderer.DrawDistance(GroundLayer.Cover) < GroundScatterRenderer.DrawDistance(GroundLayer.Accent));
        Assert.True(GroundScatterRenderer.DrawDistance(GroundLayer.Accent) < GroundScatterRenderer.DrawDistance(GroundLayer.Landmark));
    }

    [Fact]
    public void ALandmark_IsDrawnFartherOut_ThanADecalOrCover()
    {
        Vector3 focus = new(50f, 1.5f, 50f);
        ViewFrustum frustum = IsoFrustum(focus);

        // A frustum that holds the whole map isolates the distance rule: 30 units out is past the decal and
        // cover distance and inside the landmark's.
        frustum = ViewFrustum.FromViewAndProjection(Matrix4.Identity, Matrix4.CreateOrthographicOffCenter(-500f, 500f, -500f, 500f, -500f, 500f));
        int seen = 0;
        for (int degrees = 0; degrees < 360; degrees += 5)
        {
            float a = MathHelper.DegreesToRadians(degrees);
            GroundPlacement at = new(0, new Vector3(focus.X + (30f * MathF.Cos(a)), 1f, focus.Z + (30f * MathF.Sin(a))), 0f, 1f);
            Assert.False(GroundScatterRenderer.IsVisible(at, Asset(GroundLayer.Decal), focus, frustum));
            Assert.False(GroundScatterRenderer.IsVisible(at, Asset(GroundLayer.Cover), focus, frustum));
            seen += GroundScatterRenderer.IsVisible(at, Asset(GroundLayer.Landmark, 1.6f, 4f), focus, frustum) ? 1 : 0;
        }

        Assert.Equal(72, seen);
    }

    [Fact]
    public void AnAsset_BeyondItsDistance_OrOffToTheSide_IsCulled()
    {
        Vector3 focus = new(50f, 1.5f, 50f);
        ViewFrustum frustum = IsoFrustum(focus);

        Assert.False(GroundScatterRenderer.IsVisible(new GroundPlacement(0, new Vector3(50f + 60f, 1f, 50f), 0f, 1f), Asset(GroundLayer.Landmark), focus, frustum));
        // Inside the draw distance but off to the side of the view.
        Assert.False(GroundScatterRenderer.IsVisible(new GroundPlacement(0, new Vector3(50f + 21f, 1f, 50f - 21f), 0f, 1f), Asset(GroundLayer.Accent), focus, frustum));
    }

    [Fact]
    public void ACell_IsCulledByDistance_AndByTheFrustum()
    {
        Vector3 focus = new(50f, 1.5f, 50f);
        ViewFrustum frustum = IsoFrustum(focus);
        GroundScatterIndex.Cell near = new(new Vector3(48f, 1f, 48f), new Vector3(52f, 2f, 52f));
        GroundScatterIndex.Cell far = new(new Vector3(148f, 1f, 148f), new Vector3(152f, 2f, 152f));
        GroundScatterIndex.Cell side = new(new Vector3(70f, 1f, 28f), new Vector3(74f, 2f, 32f));

        Assert.True(GroundScatterRenderer.CellVisible(near, 24f, focus, frustum));
        Assert.False(GroundScatterRenderer.CellVisible(far, 24f, focus, frustum));
        Assert.False(GroundScatterRenderer.CellVisible(side, 42f, focus, frustum));
    }

    [Fact]
    public void TheIndex_RebuildsOnlyWhenThePropLayoutRebuilds_OrTheSetChanges()
    {
        (PropIndex props, StaticGround ground) = GroundTestSets.World(WorldDefaults.StartupFloorSize);
        GroundAssetSet set = GroundTestSets.Synthetic();
        GroundScatterIndex index = new();

        index.Update(props, set);
        index.Update(props, set);
        Assert.Equal(1, index.RebuildCount);
        Assert.NotEmpty(index.Placements);

        props.Rebuild(ground, []);
        index.Update(props, set);
        index.Update(props, set);
        Assert.Equal(2, index.RebuildCount);

        index.Update(props, GroundTestSets.Synthetic());
        Assert.Equal(3, index.RebuildCount);
    }

    [Fact]
    public void TheIndex_GroupsEveryPlacementIntoExactlyOneBlock_InACellThatHoldsIt()
    {
        (PropIndex props, _) = GroundTestSets.World(WorldDefaults.ServerNewWorldSize);
        GroundAssetSet set = GroundTestSets.Synthetic();
        GroundScatterIndex index = new();
        index.Update(props, set);

        int covered = 0;
        int previousAsset = -1;
        foreach (GroundScatterIndex.Block block in index.Blocks)
        {
            Assert.True(block.Asset >= previousAsset, "blocks are sorted by asset");
            previousAsset = block.Asset;
            Assert.Equal(covered, block.Start);
            covered += block.Count;
            GroundScatterIndex.Cell cell = index.Cells[block.Cell];
            for (int i = block.Start; i < block.Start + block.Count; i++)
            {
                GroundPlacement p = index.Placements[i];
                Assert.Equal(block.Asset, p.Asset);
                GroundAsset asset = set.Assets[p.Asset];
                float r = asset.Radius * p.Scale;
                Assert.True(p.Position.X - r >= cell.Min.X - 1e-3f && p.Position.X + r <= cell.Max.X + 1e-3f);
                Assert.True(p.Position.Z - r >= cell.Min.Z - 1e-3f && p.Position.Z + r <= cell.Max.Z + 1e-3f);
                Assert.True(p.Position.Y >= cell.Min.Y && p.Position.Y + (asset.Height * p.Scale) <= cell.Max.Y);
            }
        }

        Assert.Equal(index.Placements.Count, covered);
        Assert.True(index.Cells.Count < index.Blocks.Count, "assets share cells: fewer cells than (asset, cell) runs");
    }

    [Fact]
    public void TheIndex_WithAnEmptySet_HoldsNothing()
    {
        (PropIndex props, _) = GroundTestSets.World(WorldDefaults.StartupFloorSize);
        GroundScatterIndex index = new();

        index.Update(props, GroundAssetSet.Empty);

        Assert.Empty(index.Placements);
        Assert.Empty(index.Blocks);
        Assert.Empty(index.Cells);
    }

    [Fact]
    public void TheIndex_SteadyStateUpdate_AllocatesNothing()
    {
        (PropIndex props, _) = GroundTestSets.World(WorldDefaults.StartupFloorSize);
        GroundAssetSet set = GroundTestSets.Synthetic();
        GroundScatterIndex index = new();
        index.Update(props, set);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            index.Update(props, set);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
