using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// A held fire sits a hand's width from its carrier, so the carrier's own body must not be drawn into that fire's shadow
// tiles (it would throw a huge shadow over the torch's pool); every other light and every other actor keeps casting.
public sealed class HeldLightShadowCarrierTests
{
    private static readonly Matrix4 View = Matrix4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
    private static readonly Matrix4 Projection =
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60f), 16f / 9f, 0.1f, 500f);

    [Fact]
    public void SelectNearest_CarriesEachLightsSeedToItsListIndex()
    {
        PointLight[] lights =
        [
            new(new Vector3(0f, 0f, -9f), 8f, Vector3.One, FlickerSeed: 900),
            new(new Vector3(0f, 0f, -3f), 8f, Vector3.One, FlickerSeed: 7),
            new(new Vector3(0f, 0f, -6f), 8f, Vector3.One),
        ];
        LightList list = default;
        int[] seeds = new int[LightList.MaxLights];

        LightSelection.SelectNearest(
            lights, default, Vector3.Zero, ViewFrustum.FromViewAndProjection(View, Projection), ref list, seeds);

        Assert.Equal(3, list.Count);
        Assert.Equal([7, 0, 900], seeds[..3]);
    }

    [Fact]
    public void FillSlots_TagsEachCastingFireWithTheSeedOfItsOwnListEntry()
    {
        LightList list = default;
        list.Count = 2;
        list.PositionRadius[0] = new Vector4(9f, 1f, 9f, 8f);
        list.Color[0] = new Vector4(1f, 0.6f, 0.2f, 0f);
        list.PositionRadius[1] = new Vector4(1f, 1f, 1f, 8f);
        list.Color[1] = new Vector4(1f, 0.6f, 0.2f, 0f);
        int[] seeds = new int[LightList.MaxLights];
        seeds[0] = 0;
        seeds[1] = 42;
        ShadowFrame frame = ShadowFrame.None;

        FireShadowAtlas.FillSlots(in list, Vector3.Zero, 4, 1f, ref frame, seeds);

        Assert.Equal(2, frame.FireCount);
        Assert.Equal(1, frame.Fires[0].LightIndex);
        Assert.Equal(42, frame.Fires[0].CarrierSeed);
        Assert.Equal(0, frame.Fires[1].CarrierSeed);
    }

    [Fact]
    public void OnlyTheCarrierIsExcluded_AndOnlyFromItsOwnLight()
    {
        Assert.True(ShadowActorCasters.IsExcluded(ownerActorId: 42, excludeOwner: 42));
        Assert.False(ShadowActorCasters.IsExcluded(ownerActorId: 43, excludeOwner: 42));
        // A placed fire (seed 0) excludes nobody, and an unowned caster (enemy, NPC: owner 0) is never excluded.
        Assert.False(ShadowActorCasters.IsExcluded(ownerActorId: 42, excludeOwner: 0));
        Assert.False(ShadowActorCasters.IsExcluded(ownerActorId: 0, excludeOwner: 0));
        Assert.False(ShadowActorCasters.IsExcluded(ownerActorId: 0, excludeOwner: 42));
    }

    [Fact]
    public void TheCasterViewCarriesTheExclusion_AndDefaultsToNone()
    {
        Matrix4 view = View;
        Matrix4 projection = Projection;
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(view, projection);

        Assert.Equal(0, new ShadowCasterView(in view, in projection, in frustum, Vector3.Zero).ExcludeOwner);
        Assert.Equal(5, new ShadowCasterView(in view, in projection, in frustum, Vector3.Zero, excludeOwner: 5).ExcludeOwner);
    }
}
