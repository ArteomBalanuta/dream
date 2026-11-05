using System.IO;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Simulation.Inventory;

namespace Dream.Tests.Rendering.Lighting;

// Wave T2b: the point light each held-light kind casts.
public sealed class HeldLightCatalogTests
{
    [Fact]
    public void Torch_AndLantern_MatchThePlan()
    {
        // The torch was raised toward the brazier's 3.0 for A3 (the actor path is linear, so the
        // figure's internal spread scales with the gain). The radius stays 8 on purpose: the
        // brazier's and the lamp's 10 is what FxSimulation.TryClassify matches for the fire halo.
        Assert.True(HeldLightCatalog.TryResolve(HeldLightKind.Torch, out Vector3 torchColor, out float torchRadius));
        AssertClose(new Vector3(1.0f, 0.62f, 0.32f) * 2.1f, torchColor);
        Assert.Equal(2.1f, HeldLightCatalog.TorchIntensity);
        Assert.Equal(8f, torchRadius);
        Assert.True(HeldLightCatalog.TorchIntensity > 1.6f, "A3 needs the brighter torch.");

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

        Assert.Equal(0.25f, HeldLightCatalog.TorchFlickerAmplitude);
        Assert.InRange(min, 1f - HeldLightCatalog.TorchFlickerAmplitude - 1e-5f, 1f);
        Assert.InRange(max, 1f, 1f + HeldLightCatalog.TorchFlickerAmplitude + 1e-5f);
        Assert.True(max - min > 0.30f, "The torch should visibly flicker.");
        // Different seeds are out of step.
        Assert.NotEqual(
            HeldLightCatalog.Flicker(HeldLightKind.Torch, 1.0, seed: 1),
            HeldLightCatalog.Flicker(HeldLightKind.Torch, 1.0, seed: 2));
    }

    [Fact]
    public void TryCreate_PlacesTheLight_WithTheFlickeredColorRadiusAndPosition()
    {
        Vector3 position = new(3f, 4f, 5f);
        Assert.True(HeldLightCatalog.TryCreate(HeldLightKind.Torch, position, 2.5, 11, out PointLight light));
        FireFlickerSample sample = FireFlicker.Sample(FireFlickerKind.HeldTorch, position, 2.5, 11);
        AssertClose(position + sample.Offset, light.Position);
        Assert.Equal(HeldLightCatalog.TorchRadius * sample.RadiusScale, light.Radius, 5);
        AssertClose(HeldLightCatalog.TorchColor * sample.Intensity, light.Color);
        Assert.Equal(HeldLightCatalog.Flicker(HeldLightKind.Torch, 2.5, 11), sample.Intensity);

        Assert.True(HeldLightCatalog.TryCreate(HeldLightKind.Lantern, position, 2.5, 11, out PointLight lantern));
        Assert.Equal(position, lantern.Position);
        Assert.Equal(HeldLightCatalog.LanternRadius, lantern.Radius);
        Assert.Equal(HeldLightCatalog.LanternColor, lantern.Color);
    }

    // The fire halo packs light.Color * tint * fade into a byte. The halo has no flicker of its own,
    // so exactly one factor (peak 1 + A) is in that product. These pin the derivation, not one
    // number, so raising any single constant fails here instead of in a capture.
    [Fact]
    public void TheTorch_StaysUnderTheIntensityAtWhichItsHaloClips()
    {
        float peak = 1f + HeldLightCatalog.TorchFlickerAmplitude;
        Assert.Equal(1f / (FxSimulation.HeldHaloTint.X * peak), HeldLightCatalog.MaxUnclippedTorchIntensity, 4);
        Assert.Equal(2.1333f, HeldLightCatalog.MaxUnclippedTorchIntensity, 3);

        float peakRed = HeldLightCatalog.TorchIntensity * FxSimulation.HeldHaloTint.X * peak;
        Assert.True(HeldLightCatalog.TorchIntensity <= HeldLightCatalog.MaxUnclippedTorchIntensity);
        Assert.True(peakRed <= 0.99f, $"peak torch halo red {peakRed:F3}");
        Assert.True(2.3f > HeldLightCatalog.MaxUnclippedTorchIntensity);
    }

    [Fact]
    public void TheBrazier_StaysUnderTheIntensityAtWhichItsHaloClips()
    {
        float peak = 1f + FireFlicker.BrazierAmplitude;
        Assert.Equal(1f / (FxSimulation.LampHaloTint.X * peak), HeldLightCatalog.MaxUnclippedBrazierIntensity, 4);
        float peakRed = PlacedLightIndex.BrazierColor.X * FxSimulation.LampHaloTint.X * peak;
        Assert.True(PlacedLightIndex.BrazierColor.X <= HeldLightCatalog.MaxUnclippedBrazierIntensity);
        Assert.True(peakRed <= 0.99f, $"peak brazier halo red {peakRed:F3}");
        Assert.True(PlacedLightIndex.BrazierColor.X * FxSimulation.LampHaloTint.X * (1f + FireFlicker.BrazierAmplitude) <= 1.0f);
        Assert.True(HeldLightCatalog.TorchColor.X * FxSimulation.HeldHaloTint.X * (1f + HeldLightCatalog.TorchFlickerAmplitude) <= 1.0f);
    }

    [Fact]
    public void TheTorchRadius_FlickersButStaysATorchToTheFxClassifier()
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int frame = 0; frame < 20000; frame++)
        {
            Assert.True(HeldLightCatalog.TryCreate(HeldLightKind.Torch, new Vector3(1f, 2f, 3f), frame / 60.0, 5, out PointLight light));
            min = MathF.Min(min, light.Radius);
            max = MathF.Max(max, light.Radius);
        }

        Assert.InRange(min, 7.29f, 7.45f);
        Assert.InRange(max, 8.55f, 8.71f);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "dream", "Rendering")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        return File.ReadAllText(Path.Combine(new[] { root! }.Concat(parts).ToArray()));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length < 1e-5f, $"Expected {expected}, got {actual}.");
    }
}
