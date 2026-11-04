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

    // The fire halo packs light.Color * HeldHaloTint * gain into a byte, and BOTH flickers are in
    // that product: the PointLight the FX collector is handed already carries the light's own
    // flicker (TryCreate returns color * Flicker, peak 1 + TorchFlickerAmplitude), and the halo
    // multiplies by its own on top. At 2.3 the peak red was 1.071, so the channel pinned flat at
    // flicker peaks and the halo stopped responding to its own flicker. These pin the derivation,
    // not one number, so raising any single constant fails here instead of in a capture.
    [Fact]
    public void TheTorch_StaysUnderTheIntensityAtWhichItsHaloClips()
    {
        float lightPeak = 1f + HeldLightCatalog.TorchFlickerAmplitude;
        float haloPeak = FxSimulation.TorchHaloFlickerPeak;

        Assert.Equal(1.08f, lightPeak, 4);
        Assert.Equal(1.15f, haloPeak, 4);
        Assert.Equal(1f / (FxSimulation.HeldHaloTint.X * lightPeak * haloPeak), HeldLightCatalog.MaxUnclippedTorchIntensity, 4);
        Assert.Equal(2.1471f, HeldLightCatalog.MaxUnclippedTorchIntensity, 3);

        float peakRed = HeldLightCatalog.TorchIntensity * FxSimulation.HeldHaloTint.X * lightPeak * haloPeak;
        Assert.True(
            HeldLightCatalog.TorchIntensity <= HeldLightCatalog.MaxUnclippedTorchIntensity,
            $"TorchIntensity {HeldLightCatalog.TorchIntensity} clips the halo's red channel at " +
            $"{peakRed:F3}; the ceiling is {HeldLightCatalog.MaxUnclippedTorchIntensity:F3}.");
        Assert.True(peakRed <= 1f, $"peak halo red {peakRed:F3} saturates");
        // The 2.3 that shipped on the branch would have failed this.
        Assert.True(2.3f > HeldLightCatalog.MaxUnclippedTorchIntensity);
    }

    // The literals the halo's flicker is built from, so TorchHaloFlickerPeak cannot drift from the
    // sines FxSimulation actually sums.
    [Fact]
    public void TheHaloFlickerPeak_IsTheSumOfItsOwnSines()
    {
        Assert.Equal(
            1f + FxSimulation.TorchHaloFlickerFast + FxSimulation.TorchHaloFlickerMid + FxSimulation.TorchHaloFlickerSlow,
            FxSimulation.TorchHaloFlickerPeak,
            5);
        string source = ReadRepoFile("dream", "Rendering", "Effects", "FxSimulation.cs");
        Assert.Contains("TorchHaloFlickerFast * MathF.Sin", source, StringComparison.Ordinal);
        Assert.Contains("TorchHaloFlickerMid * MathF.Sin", source, StringComparison.Ordinal);
        Assert.Contains("TorchHaloFlickerSlow * MathF.Sin", source, StringComparison.Ordinal);
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
