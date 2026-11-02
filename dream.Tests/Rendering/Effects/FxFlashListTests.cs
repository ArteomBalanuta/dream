using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

public class FxFlashListTests
{
    private static readonly Vector3 Here = new(3f, 1f, 3f);

    private static LightCollector Collect(FxFlashList flashes, float night = 1f, int existing = 0)
    {
        LightCollector lights = new();
        for (int i = 0; i < existing; i++)
        {
            lights.Add(new PointLight(new Vector3(i, 0f, 0f), LampLightIndex.LampRadius, Vector3.One));
        }

        flashes.AppendTo(lights, night);
        return lights;
    }

    [Fact]
    public void Radii_Never_Match_A_Halo_Class()
    {
        foreach (FxFlashKind kind in Enum.GetValues<FxFlashKind>())
        {
            float radius = FxFlashList.Radius(kind);
            Assert.NotEqual(HeldLightCatalog.LanternRadius, radius);
            Assert.NotEqual(HeldLightCatalog.TorchRadius, radius);
            Assert.NotEqual(LampLightIndex.LampRadius, radius);
        }
    }

    [Fact]
    public void A_Fresh_Flash_Adds_Its_Peak_Colour_And_Fades_To_Nothing()
    {
        FxFlashList flashes = new();
        flashes.Trigger(Here, FxFlashKind.Death);

        LightCollector lights = Collect(flashes);
        Assert.Equal(1, lights.Count);
        PointLight light = lights.Lights[0];
        Assert.Equal(Here, light.Position);
        Assert.Equal(5.5f, light.Radius);
        Assert.Equal(2.2f, light.Color.X, 3);
        Assert.Equal(2.2f * 0.52f, light.Color.Y, 3);

        flashes.Advance(0.1f);
        flashes.Advance(0.1f);
        PointLight half = Collect(flashes).Lights[0];
        Assert.Equal(2.2f * 0.25f, half.Color.X, 3);

        flashes.Advance(0.1f);
        flashes.Advance(0.1f);
        Assert.Equal(0, Collect(flashes).Count);
        Assert.Equal(0, flashes.Live);
    }

    [Fact]
    public void Daylight_Scales_The_Flash_Down_To_Thirty_Percent()
    {
        FxFlashList flashes = new();
        flashes.Trigger(Here, FxFlashKind.Hit);

        Assert.Equal(0.9f, Collect(flashes, night: 1f).Lights[0].Color.X, 3);
        Assert.Equal(0.9f * 0.3f, Collect(flashes, night: 0f).Lights[0].Color.X, 3);
    }

    [Fact]
    public void A_Flash_Never_Displaces_A_Collected_Light()
    {
        FxFlashList flashes = new();
        flashes.Trigger(Here, FxFlashKind.Death);
        flashes.Trigger(Here + new Vector3(10f, 0f, 0f), FxFlashKind.Critical);
        flashes.Trigger(Here + new Vector3(20f, 0f, 0f), FxFlashKind.Hit);

        Assert.Equal(LightList.MaxLights, Collect(flashes, existing: LightList.MaxLights).Count);
        Assert.Equal(LightList.MaxLights + 40, Collect(flashes, existing: LightList.MaxLights + 40).Count);

        LightCollector oneFree = Collect(flashes, existing: LightList.MaxLights - 1);
        Assert.Equal(LightList.MaxLights, oneFree.Count);
        Assert.Equal(5.5f, oneFree.Lights[LightList.MaxLights - 1].Radius);

        LightCollector twoFree = Collect(flashes, existing: LightList.MaxLights - 2);
        Assert.Equal(LightList.MaxLights, twoFree.Count);
        Assert.Equal(5.5f, twoFree.Lights[LightList.MaxLights - 2].Radius);
        Assert.Equal(4.5f, twoFree.Lights[LightList.MaxLights - 1].Radius);
    }

    [Fact]
    public void A_Nearby_Hit_Cannot_Restart_A_Young_Flash_But_A_Death_Always_Does()
    {
        FxFlashList flashes = new();
        flashes.Trigger(Here, FxFlashKind.Critical);
        flashes.Advance(0.05f);

        flashes.Trigger(Here + new Vector3(1f, 0f, 0f), FxFlashKind.Critical);
        Assert.Equal(1, flashes.Live);
        PointLight kept = Collect(flashes).Lights[0];
        Assert.Equal(Here, kept.Position);
        Assert.True(kept.Color.X < 1.3f);

        flashes.Trigger(Here + new Vector3(1f, 0f, 0f), FxFlashKind.Death);
        Assert.Equal(1, flashes.Live);
        Assert.Equal(2.2f, Collect(flashes).Lights[0].Color.X, 3);
    }

    [Fact]
    public void A_Strong_Enough_Old_Enough_Trigger_Restarts_A_Nearby_Flash()
    {
        FxFlashList flashes = new();
        flashes.Trigger(Here, FxFlashKind.Hit);
        flashes.Advance(0.1f);

        flashes.Trigger(Here, FxFlashKind.Hit);
        Assert.Equal(1, flashes.Live);
        Assert.Equal(0.9f, Collect(flashes).Lights[0].Color.X, 3);
    }

    [Fact]
    public void A_Full_List_Replaces_The_Weakest_Only_For_An_At_Least_As_Strong_Trigger()
    {
        FxFlashList flashes = new();
        for (int i = 0; i < FxFlashList.Capacity; i++)
        {
            flashes.Trigger(new Vector3(i * 10f, 1f, 0f), FxFlashKind.Critical);
        }

        flashes.Trigger(new Vector3(100f, 1f, 0f), FxFlashKind.Incoming);
        Assert.Equal(FxFlashList.Capacity, flashes.Live);
        Assert.DoesNotContain(Collect(flashes).Lights.ToArray(), light => light.Position.X == 100f);

        flashes.Trigger(new Vector3(100f, 1f, 0f), FxFlashKind.Death);
        Assert.Equal(FxFlashList.Capacity, flashes.Live);
        Assert.Contains(Collect(flashes).Lights.ToArray(), light => light.Position.X == 100f);
    }

    [Fact]
    public void Non_Finite_Positions_And_Huge_Frame_Times_Are_Ignored()
    {
        FxFlashList flashes = new();
        flashes.Trigger(new Vector3(float.NaN, 0f, 0f), FxFlashKind.Hit);
        Assert.Equal(0, flashes.Live);

        flashes.Trigger(Here, FxFlashKind.Death);
        flashes.Advance(5f);
        Assert.Equal(1, flashes.Live);
        flashes.Advance(float.NaN);
        flashes.Advance(-1f);
        Assert.Equal(1, flashes.Live);
    }

    [Fact]
    public void Trigger_Advance_And_AppendTo_On_A_Full_Collector_Allocate_Nothing()
    {
        FxFlashList flashes = new();
        LightCollector full = new();
        for (int i = 0; i < LightCollector.Capacity; i++)
        {
            full.Add(new PointLight(new Vector3(i, 0f, 0f), LampLightIndex.LampRadius, Vector3.One));
        }

        LightCollector roomy = new();
        int tick = 0;

        AllocationAssert.AtMost(0, () =>
        {
            tick++;
            flashes.Trigger(new Vector3(tick % 7 * 3f, 1f, 0f), (FxFlashKind)(tick & 3));
            flashes.Advance(0.016f);
            flashes.AppendTo(full, 1f);
            roomy.Clear();
            roomy.Add(new PointLight(Vector3.Zero, LampLightIndex.LampRadius, Vector3.One));
            flashes.AppendTo(roomy, 0.5f);
        });
    }
}
