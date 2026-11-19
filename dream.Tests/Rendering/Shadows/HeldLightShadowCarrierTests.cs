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
    public void SelectNearest_CarriesEachLightsCarrierToItsListIndex()
    {
        PointLight[] lights =
        [
            new(new Vector3(0f, 0f, -9f), 8f, Vector3.One, FlickerSeed: 5, CarrierActorId: 900),
            new(new Vector3(0f, 0f, -3f), 8f, Vector3.One, FlickerSeed: 5, CarrierActorId: 7),
            new(new Vector3(0f, 0f, -6f), 8f, Vector3.One),
        ];
        LightList list = default;
        int[] carriers = new int[LightList.MaxLights];

        LightSelection.SelectNearest(
            lights, default, Vector3.Zero, ViewFrustum.FromViewAndProjection(View, Projection), ref list, carriers);

        Assert.Equal(3, list.Count);
        Assert.Equal([7, 0, 900], carriers[..3]);
    }

    [Fact]
    public void FillSlots_TagsEachCastingFireWithTheCarrierOfItsOwnListEntry()
    {
        LightList list = default;
        list.Count = 2;
        list.PositionRadius[0] = new Vector4(9f, 1f, 9f, 8f);
        list.Color[0] = new Vector4(1f, 0.6f, 0.2f, 0f);
        list.PositionRadius[1] = new Vector4(1f, 1f, 1f, 8f);
        list.Color[1] = new Vector4(1f, 0.6f, 0.2f, 0f);
        int[] carriers = new int[LightList.MaxLights];
        carriers[0] = 0;
        carriers[1] = 42;
        ShadowFrame frame = ShadowFrame.None;

        FireShadowAtlas.FillSlots(in list, Vector3.Zero, 4, 1f, ref frame, carriers);

        Assert.Equal(2, frame.FireCount);
        Assert.Equal(1, frame.Fires[0].LightIndex);
        Assert.Equal(42, frame.Fires[0].CarrierActorId);
        Assert.Equal(0, frame.Fires[1].CarrierActorId);
    }

    [Fact]
    public void OnlyTheCarrierIsExcluded_AndOnlyFromItsOwnLight()
    {
        Assert.True(ShadowActorCasters.IsExcluded(ownerActorId: 42, excludeOwner: 42));
        Assert.False(ShadowActorCasters.IsExcluded(ownerActorId: 43, excludeOwner: 42));
        // A light with no carrier excludes nobody, and an unowned caster (enemy, NPC: owner 0) is never excluded.
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

    [Fact]
    public void APlacedFiresFlickerSeedIsNotACarrier()
    {
        // A placed fire's FlickerSeed is a position hash that can equal a live player id; it must exclude nobody.
        PointLight[] lights = [new(new Vector3(0f, 0f, -4f), 8f, Vector3.One, FlickerSeed: 42)];
        LightList list = default;
        int[] carriers = new int[LightList.MaxLights];
        carriers[0] = -1;

        LightSelection.SelectNearest(
            lights, default, Vector3.Zero, ViewFrustum.FromViewAndProjection(View, Projection), ref list, carriers);

        Assert.Equal(1, list.Count);
        Assert.Equal(0, carriers[0]);
        Assert.False(ShadowActorCasters.IsExcluded(ownerActorId: 42, excludeOwner: carriers[0]));
    }

    [Fact]
    public void CarriersStayAlignedWhenTheListIsFullAndTheFarthestLightIsDisplaced()
    {
        int total = LightList.MaxLights + 2;
        PointLight[] lights = new PointLight[total];
        for (int i = 0; i < total; i++)
        {
            // Farthest first; carrier id 100 + i, flicker seed unrelated.
            lights[i] = new PointLight(new Vector3(0f, 0f, -(total - i) - 1f), 8f, Vector3.One, FlickerSeed: 3, CarrierActorId: 100 + i);
        }

        LightList list = default;
        int[] carriers = new int[LightList.MaxLights];
        LightSelection.SelectNearest(
            lights, default, Vector3.Zero, ViewFrustum.FromViewAndProjection(View, Projection), ref list, carriers);

        Assert.Equal(LightList.MaxLights, list.Count);
        for (int slot = 0; slot < LightList.MaxLights; slot++)
        {
            // Nearest first: the last input is nearest. The slot's carrier must belong to the slot's position.
            int input = total - 1 - slot;
            Assert.Equal(lights[input].Position.Z, list.PositionRadius[slot].Z);
            Assert.Equal(100 + input, carriers[slot]);
        }
    }
}
