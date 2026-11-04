using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Props;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Props;

// The prop renderer submits only what can be seen: within the draw distance of the focus and touching the
// frustum of the isometric camera. The heaviest model (the oak) is 62k triangles, so this is what keeps the
// scene from drawing every tree on the map.
public class PropRendererCullingTests
{
    private static ViewFrustum IsoFrustum(Vector3 focus)
    {
        Camera camera = new(Vector3.Zero);
        IsometricView.ResolveInto(focus, camera);
        return ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), IsometricView.CreateProjection(1440f / 884f));
    }

    [Fact]
    public void AProp_AtTheFocus_IsVisible()
    {
        Vector3 focus = new(8f, 1.5f, 8f);
        PropPlacement p = new(PropKind.Cottage, new Vector3(8f, 1f, 8f), 0f);

        Assert.True(PropRenderer.IsVisible(p, focus, IsoFrustum(focus)));
    }

    [Fact]
    public void AProp_FarOutsideTheView_IsCulled_ByDistanceAndByTheFrustum()
    {
        Vector3 focus = new(8f, 1.5f, 8f);
        ViewFrustum frustum = IsoFrustum(focus);

        // Beyond the draw distance whatever the frustum says.
        Assert.False(PropRenderer.IsVisible(new PropPlacement(PropKind.DeadOak, new Vector3(8f + PropRenderer.DrawDistance + 10f, 1f, 8f), 0f), focus, frustum));
        // Inside the draw distance but off to the side of the view: 30 units along the screen's horizontal.
        Assert.False(PropRenderer.IsVisible(new PropPlacement(PropKind.DeadOak, new Vector3(8f + 21f, 1f, 8f - 21f), 0f), focus, frustum));
    }

    [Fact]
    public void ABigProp_JustOffScreen_IsStillDrawn_WhenItsBoxReachesIn()
    {
        Vector3 focus = new(8f, 1.5f, 8f);
        ViewFrustum frustum = IsoFrustum(focus);

        // Walk a cottage out along the screen's right edge until it is culled: the last drawn spot must be further out
        // than the same walk for a barrel, because the cottage's box is larger.
        static float EdgeFor(PropKind kind, Vector3 focus, ViewFrustum frustum)
        {
            float last = 0f;
            for (float d = 0f; d < 40f; d += 0.25f)
            {
                PropPlacement p = new(kind, new Vector3(focus.X + d, 1f, focus.Z - d), 0f);
                if (PropRenderer.IsVisible(p, focus, frustum))
                {
                    last = d;
                }
            }

            return last;
        }

        float cottage = EdgeFor(PropKind.Cottage, focus, frustum);
        float barrel = EdgeFor(PropKind.RustBarrel, focus, frustum);
        Assert.True(barrel > 5f, $"a barrel at 5 units off the focus is on screen (edge {barrel})");
        Assert.True(cottage > barrel, $"the cottage edge {cottage} should reach further than the barrel's {barrel}");
        Assert.True(cottage < 40f);
    }

    [Fact]
    public void EveryKind_HasBoundsAndAFootprint()
    {
        foreach (PropKind kind in Enum.GetValues<PropKind>())
        {
            PropBounds b = PropCatalog.Bounds(kind);
            PropFootprint f = PropCatalog.Footprint(kind);
            Assert.True(b.HorizontalRadius > 0f && b.Height > 0f, $"{kind} bounds");
            Assert.True(b.HorizontalRadius >= MathF.Max(f.HalfWidth, f.HalfDepth) * 0.9f, $"{kind}: the culling sphere is smaller than the footprint it stands on");
        }
    }
}
