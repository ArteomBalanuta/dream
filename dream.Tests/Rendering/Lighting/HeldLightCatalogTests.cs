using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Lighting;
using Dream.Simulation.Inventory;

namespace Dream.Tests.Rendering.Lighting;

// Wave T2b: the point light each held-light kind casts.
public sealed class HeldLightCatalogTests
{
    [Fact]
    public void Torch_AndLantern_MatchThePlan()
    {
        Assert.True(HeldLightCatalog.TryResolve(HeldLightKind.Torch, out Vector3 torchColor, out float torchRadius));
        AssertClose(new Vector3(1.6f, 0.992f, 0.512f), torchColor);
        Assert.Equal(8f, torchRadius);

        Assert.True(HeldLightCatalog.TryResolve(HeldLightKind.Lantern, out Vector3 lanternColor, out float lanternRadius));
        AssertClose(new Vector3(1.1f, 0.88f, 0.605f), lanternColor);
        Assert.Equal(6f, lanternRadius);
    }

    [Fact]
    public void None_AndUnknownKinds_CastNoLight()
    {
        Assert.False(HeldLightCatalog.TryResolve(HeldLightKind.None, out _, out _));
        Assert.False(HeldLightCatalog.TryResolve((HeldLightKind)99, out _, out _));
        Assert.False(HeldLightCatalog.TryCreate(HeldLightKind.None, Vector3.Zero, 1.0, 0, out _));
    }

    [Fact]
    public void Flicker_IsDeterministic_Bounded_AndOnlyForTheTorch()
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int frame = 0; frame < 2000; frame++)
        {
            double time = frame / 60.0;
            float flicker = HeldLightCatalog.Flicker(HeldLightKind.Torch, time, seed: 7);
            Assert.Equal(flicker, HeldLightCatalog.Flicker(HeldLightKind.Torch, time, seed: 7));
            min = MathF.Min(min, flicker);
            max = MathF.Max(max, flicker);
            Assert.Equal(1f, HeldLightCatalog.Flicker(HeldLightKind.Lantern, time, seed: 7));
        }

        Assert.InRange(min, 1f - HeldLightCatalog.TorchFlickerAmplitude - 1e-5f, 1f);
        Assert.InRange(max, 1f, 1f + HeldLightCatalog.TorchFlickerAmplitude + 1e-5f);
        Assert.True(max - min > 0.05f, "The torch should visibly flicker.");
        // Different seeds are out of step.
        Assert.NotEqual(
            HeldLightCatalog.Flicker(HeldLightKind.Torch, 1.0, seed: 1),
            HeldLightCatalog.Flicker(HeldLightKind.Torch, 1.0, seed: 2));
    }

    [Fact]
    public void TryCreate_PlacesTheLight_WithTheFlickeredColor()
    {
        Vector3 position = new(3f, 4f, 5f);
        Assert.True(HeldLightCatalog.TryCreate(HeldLightKind.Torch, position, 2.5, 11, out PointLight light));
        Assert.Equal(position, light.Position);
        Assert.Equal(HeldLightCatalog.TorchRadius, light.Radius);
        AssertClose(HeldLightCatalog.TorchColor * HeldLightCatalog.Flicker(HeldLightKind.Torch, 2.5, 11), light.Color);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length < 1e-5f, $"Expected {expected}, got {actual}.");
    }
}
