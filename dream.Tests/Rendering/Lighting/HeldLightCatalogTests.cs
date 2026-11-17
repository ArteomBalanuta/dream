using System.IO;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
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
        // The actor id rides along, so a consumer that only has the light recovers this very sample.
        Assert.Equal(11, light.FlickerSeed);
        Assert.Equal(sample.Intensity, FireFlicker.IntensityOf(in light, 2.5));

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

    // The torch FLAME sprite packs cull * flameNight * min(flicker, cap) * tint into bytes. Before the cap,
    // flicker (peak 1.25) pinned every channel for the upper half of the cycle. Worst case: night, cull 1.
    [Fact]
    public void TheTorchFlame_NeverPinsAChannel_AndTheCeilingIsDerived()
    {
        Assert.Equal(1f / FxSimulation.FlameMaxChannelGain, HeldLightCatalog.MaxUnclippedFlameBrightness, 5);
        Assert.Equal(1f, HeldLightCatalog.MaxUnclippedFlameBrightness, 4);

        Assert.Equal(1f * (1f - HeldLightCatalog.FlameSaturationMargin), HeldLightCatalog.FlameBrightnessCeiling, 5);

        float a = HeldLightCatalog.TorchFlickerAmplitude;
        float peak = 1f + a;
        // The byte clamp hides overshoot, so check the UNCLAMPED channel gain: night, cull 1, every flicker.
        for (float f = 1f - a; f <= peak; f += 0.005f)
        {
            float gain = FxSimulation.FlameBrightnessAt(f) * FxSimulation.FlameMaxChannelGain;
            Assert.True(gain <= HeldLightCatalog.FlameBrightnessCeiling + 1e-6f, $"flame channel {gain} overshoots at flicker {f}");
        }

        // No plateau: strictly increasing, and at least a byte per 0.01 of flicker, over the whole range.
        uint previous = FxSimulation.FlameColor(1f, 1f - a, 1f) & 0xFFu;
        for (float f = 1f - a + 0.01f; f <= peak + 1e-4f; f += 0.01f)
        {
            uint now = FxSimulation.FlameColor(1f, f, 1f) & 0xFFu;
            Assert.True(now > previous, $"flame byte plateaus at flicker {f}: {previous} -> {now}");
            previous = now;
        }

        Assert.Equal(191u, FxSimulation.FlameColor(1f, 1f - a, 1f) & 0xFFu);
        Assert.Equal(219u, FxSimulation.FlameColor(1f, 1f, 1f) & 0xFFu);
        Assert.Equal(247u, FxSimulation.FlameColor(1f, peak, 1f) & 0xFFu);

        // The sprite grows with the flicker as well.
        Assert.True(FxSimulation.FlameHalfSizeAt(peak) > FxSimulation.FlameHalfSizeAt(1f));
        Assert.True(FxSimulation.FlameHalfSizeAt(1f - a) < FxSimulation.FlameHalfSizeAt(1f));
        // Without the margin the peak would touch the byte.
        Assert.True(peak * FxSimulation.FlameMaxChannelGain > 1f);
    }

    [Fact]
    public void TheWorstCasePackedBytes_OfTheTorchFlame_TheBrazierHalo_AndTheLampHalo_StayInRange()
    {
        float peak = 1f + FireFlicker.TorchAmplitude;
        uint flame = FxSimulation.FlameColor(1f, peak, 1f);
        uint brazier = FxInstance.PackColor(
            PlacedLightIndex.BrazierColor.X * peak * FxSimulation.LampHaloTint.X, 0f, 0f, 0f);
        uint lamp = FxInstance.PackColor(
            LampLightIndex.LampColor.X * peak * FxSimulation.LampHaloTint.X, 0f, 0f, 0f);
        Assert.True((flame & 0xFFu) < 255u, $"flame {flame & 0xFFu}");
        Assert.True((brazier & 0xFFu) < 255u, $"brazier {brazier & 0xFFu}");
        Assert.True((lamp & 0xFFu) < 255u, $"lamp {lamp & 0xFFu}");
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
