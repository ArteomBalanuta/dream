using Dream.Rendering.Lighting;
using Dream.Rendering.Sky;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Lighting;

// Wave T1 tester, rendering without GL: SkyModel -> FrameLighting -> FrameLightingBuffer.ForWorld /
// ForViewModel over a whole day, and the cube and enemy shaders mirrored on the CPU, so the merged
// pipeline gives sane uniforms, a dark night and the T2d noon (SkyModelKeyframes.Noon).
public class WaveT1DayCycleLightingTests(ITestOutputHelper output)
{
    private const int Samples = 24 * 60;

    // Eye at spawn height, looking along +Z with a little yaw so the view matrix is not trivial.
    private static readonly Vector3 Eye = new(5f, 2.1f, 5f);
    private static readonly Matrix4 View = Matrix4.LookAt(Eye, Eye + new Vector3(0.3f, -0.2f, 1f), Vector3.UnitY);

    private static readonly Vector3[] FaceNormals =
    [
        Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
    ];

    private static readonly Vector3 Grass = new(0.36f, 0.62f, 0.28f);

    [Theory]
    [InlineData(0.07f)]
    [InlineData(0.15f)]
    [InlineData(0f)]
    [InlineData(1f)]
    public void EveryMinute_BothRanges_AreFinite_FogIsNonNegative_AndTheViewModelFloorApplies(float nightBrightness)
    {
        SkyModelSettings settings = new(nightBrightness);
        for (int i = 0; i < Samples; i++)
        {
            float t = i / (float)Samples;
            SkyState sky = SkyModel.Evaluate(t, settings);
            FrameLighting world = FrameLightingBuffer.ForWorld(in sky.Lighting, Eye);
            FrameLighting viewModel = FrameLightingBuffer.ForViewModel(in sky.Lighting, in View);

            foreach (FrameLighting range in new[] { world, viewModel })
            {
                AssertFinite(range, t);
                Assert.True(range.FogColor.W >= 0f, $"fog density {range.FogColor.W} at {t}");
                Assert.True(range.CameraPosition.W >= 0f, $"fog start {range.CameraPosition.W} at {t}");
                Assert.True(range.SunDirection.W >= 0f && range.MoonDirection.W >= 0f, $"negative light at {t}");
                Assert.True(range.Params.X > 0f, $"exposure {range.Params.X} at {t}");
                Assert.True(range.Params.Y >= 0f, $"actor floor {range.Params.Y} at {t}");
                AssertInUnitRange(range.FogColor.Xyz, $"fog colour at {t}");
                AssertInUnitRange(range.AmbientSky.Xyz, $"sky ambient at {t}");
                AssertInUnitRange(range.AmbientGround.Xyz, $"ground ambient at {t}");
            }

            // The world range is the frame's lighting with only the camera filled in.
            Assert.Equal(new Vector4(Eye, sky.Lighting.CameraPosition.W), world.CameraPosition);
            Assert.Equal(sky.Lighting.SunDirection, world.SunDirection);
            Assert.Equal(sky.Lighting.AmbientSky, world.AmbientSky);

            // The view-model range: eye-space directions of the same length and intensity, the
            // camera at the origin with the fog start kept, ambient floored per channel.
            Assert.Equal(Vector3.Zero, viewModel.CameraPosition.Xyz);
            Assert.Equal(sky.Lighting.CameraPosition.W, viewModel.CameraPosition.W);
            Assert.Equal(sky.Lighting.SunDirection.W, viewModel.SunDirection.W);
            Assert.Equal(1f, viewModel.SunDirection.Xyz.Length, 3);
            Assert.Equal(1f, viewModel.MoonDirection.Xyz.Length, 3);
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal(MathF.Max(sky.Lighting.AmbientSky[c], FrameLightingBuffer.ViewModelAmbientFloor), viewModel.AmbientSky[c]);
                Assert.Equal(MathF.Max(sky.Lighting.AmbientGround[c], FrameLightingBuffer.ViewModelAmbientFloor), viewModel.AmbientGround[c]);
                Assert.True(viewModel.AmbientSky[c] >= FrameLightingBuffer.ViewModelAmbientFloor);
            }

            // The rotation into eye space keeps the angle between the two lights.
            Assert.Equal(
                Vector3.Dot(world.SunDirection.Xyz, world.MoonDirection.Xyz),
                Vector3.Dot(viewModel.SunDirection.Xyz, viewModel.MoonDirection.Xyz),
                3);
        }
    }

    // The cube shader on the CPU at spawn, averaged over the six faces: midnight terrain is 5-8% of
    // noon's luminance (T2d: a dark night; T1 was 13%), noon is SkyModelKeyframes.Noon (T2d replaced the
    // legacy ReferenceNoon on purpose, see SkyModelTests), and luminance moves smoothly over the day.
    [Fact]
    public void TerrainLuminance_IsDarkAtMidnight_AndNoonIsTheNoonKeyframe()
    {
        Vector3 fragPos = new(5f, 0.5f, 5f);
        float noon = TerrainLuminance(SkyModel.Evaluate(0.5f).Lighting, fragPos);
        float midnight = TerrainLuminance(SkyModel.Evaluate(0f).Lighting, fragPos);
        float reference = TerrainLuminance(FrameLightingBuffer.ForWorld(SkyModelKeyframes.Noon, Eye), fragPos);

        float min = float.MaxValue;
        float max = 0f;
        float previous = TerrainLuminance(SkyModel.Evaluate(0f).Lighting, fragPos);
        float largestStep = 0f;
        for (int i = 1; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            FrameLighting world = FrameLightingBuffer.ForWorld(SkyModel.Evaluate(t).Lighting, Eye);
            float luminance = TerrainLuminance(world, fragPos);
            Assert.True(float.IsFinite(luminance));
            min = MathF.Min(min, luminance);
            max = MathF.Max(max, luminance);
            largestStep = MathF.Max(largestStep, MathF.Abs(luminance - previous));
            previous = luminance;
        }

        output.WriteLine($"terrain luminance: noon {noon:F4}, midnight {midnight:F4} ({midnight / noon:P1}), day min {min:F4}, max {max:F4}, largest 1-min step {largestStep:F4}");
        Assert.Equal(reference, noon, 4);
        Assert.InRange(midnight / noon, 0.05f, 0.08f);
        Assert.True(max <= noon * 1.25f, $"some time of day is much brighter ({max}) than noon ({noon})");
        Assert.True(largestStep <= 0.02f * noon, $"luminance jumps by {largestStep} in one minute");
    }

    // Enemies and players at midnight keep their brightness floor but stay well below noon.
    [Fact]
    public void ActorLuminance_AtMidnight_IsBelowNoon_AndAboveTheFloor()
    {
        Vector3 fragPos = new(7.5f, 1f, 11f);
        Vector3 fur = new(0.45f, 0.4f, 0.35f);
        FrameLighting noon = FrameLightingBuffer.ForWorld(SkyModel.Evaluate(0.5f).Lighting, Eye);
        FrameLighting midnight = FrameLightingBuffer.ForWorld(SkyModel.Evaluate(0f).Lighting, Eye);
        float noonLum = 0f;
        float nightLum = 0f;
        foreach (Vector3 normal in FaceNormals)
        {
            noonLum += Luminance(Saturate(Enemy(in noon, normal, fragPos, fur))) / FaceNormals.Length;
            nightLum += Luminance(Saturate(Enemy(in midnight, normal, fragPos, fur))) / FaceNormals.Length;
        }

        output.WriteLine($"actor luminance: noon {noonLum:F4}, midnight {nightLum:F4} ({nightLum / noonLum:P1}), floor {midnight.Params.Y:F3} x exposure {midnight.Params.X:F3}");
        Assert.True(nightLum < 0.5f * noonLum, $"actors at midnight ({nightLum}) are not much darker than at noon ({noonLum})");
        Assert.True(nightLum >= Luminance(fur) * midnight.Params.Y * midnight.Params.X * 0.99f);
    }

    // Actors must not look self-lit at night: with the same base color and position, an up face on a
    // wolf or player at midnight is at most 1.3x the terrain's (and not darker than it), while at noon
    // the day ambient scale keeps today's brighter-than-terrain look.
    [Theory]
    [InlineData(0.07f)]
    [InlineData(0.15f)]
    [InlineData(0.05f)]
    [InlineData(0.4f)]
    public void ActorToTerrainLuminance_OnUpFaces_AtMidnight_IsAtMost1Point3(float nightBrightness)
    {
        Vector3 fragPos = new(7.5f, 1f, 11f);
        Vector3 fur = new(0.45f, 0.4f, 0.35f);
        FrameLighting midnight = FrameLightingBuffer.ForWorld(SkyModel.Evaluate(0f, new SkyModelSettings(nightBrightness)).Lighting, Eye);
        FrameLighting noon = FrameLightingBuffer.ForWorld(SkyModel.Evaluate(0.5f).Lighting, Eye);

        float nightRatio = Luminance(Enemy(in midnight, Vector3.UnitY, fragPos, fur))
            / Luminance(Cube(in midnight, Vector3.UnitY, fragPos, fur));
        float noonRatio = Luminance(Enemy(in noon, Vector3.UnitY, fragPos, fur))
            / Luminance(Cube(in noon, Vector3.UnitY, fragPos, fur));
        float sideNightRatio = Luminance(Enemy(in midnight, Vector3.UnitX, fragPos, fur))
            / Luminance(Cube(in midnight, Vector3.UnitX, fragPos, fur));

        output.WriteLine($"night {nightBrightness}: actor/terrain up {nightRatio:F3}, side +X {sideNightRatio:F3}; noon up {noonRatio:F3}; scale {midnight.Params.Z}");
        Assert.True(nightRatio <= 1.3f, $"actors at midnight are {nightRatio}x terrain on up faces");
        Assert.True(nightRatio >= 1f, $"actors at midnight are darker ({nightRatio}x) than terrain on up faces");
        Assert.True(sideNightRatio <= 2f, $"actors at midnight are {sideNightRatio}x terrain on side faces");
        Assert.True(noonRatio > 1.3f, $"actors at noon ({noonRatio}x terrain) lost their day look");
    }

    // The view model at midnight never goes black: the ambient floor alone lights it.
    [Fact]
    public void ViewModel_AtMidnight_IsLitAtLeastByTheFloor()
    {
        Vector3 hand = new(0.85f, 0.7f, 0.6f);
        FrameLighting viewModel = FrameLightingBuffer.ForViewModel(SkyModel.Evaluate(0f, new SkyModelSettings(0f)).Lighting, in View);
        Vector3 eyeSpaceFragPos = new(0.3f, -0.3f, -0.6f);
        foreach (Vector3 normal in FaceNormals)
        {
            Vector3 lit = Cube(in viewModel, normal, eyeSpaceFragPos, hand);
            Assert.True(
                Luminance(lit) >= Luminance(hand) * FrameLightingBuffer.ViewModelAmbientFloor * viewModel.Params.X * 0.99f,
                $"view model face {normal} is {lit}");
        }
    }

    // T2d: at 00:00 a held torch 2 m from an up-facing ground cell lights it to at least 40% of noon's
    // up-facing light. The torch is the plan's T2b catalog value, (1.0, 0.62, 0.32) x 1.6 with radius 8,
    // held 1.3 m above the ground (the third-person anchor height), so the ground point is 2 m away and
    // 1.52 m to the side. The light is the CPU mirror of dreamPointLights (pointLights.glsl).
    [Fact]
    public void TorchAtTwoMetres_AtMidnight_GivesAtLeast40PercentOfNoonUpFacingLight()
    {
        Vector3 torchColor = new Vector3(1.0f, 0.62f, 0.32f) * 1.6f;
        const float TorchRadius = 8f;
        Vector3 ground = new(5f, 0.5f, 5f);
        float horizontal = MathF.Sqrt(2f * 2f - 1.3f * 1.3f);
        Vector3 torch = ground + new Vector3(horizontal, 1.3f, 0f);
        Assert.Equal(2f, Vector3.Distance(torch, ground), 4);

        FrameLighting noon = SkyModel.Evaluate(0.5f).Lighting;
        FrameLighting midnight = SkyModel.Evaluate(0f).Lighting;
        float noonUp = Luminance(Ambient(in noon, Vector3.UnitY) + Direct(in noon, Vector3.UnitY)) * noon.Params.X;
        float nightUp = Luminance(Ambient(in midnight, Vector3.UnitY) + Direct(in midnight, Vector3.UnitY)) * midnight.Params.X;
        float torchUp = Luminance(PointLight(Vector3.UnitY, ground, torch, TorchRadius, torchColor)) * midnight.Params.X;

        output.WriteLine($"up-facing: noon {noonUp:F4}, midnight {nightUp:F4} ({nightUp / noonUp:P1}), torch at 2 m {torchUp:F4} ({torchUp / noonUp:P1}), torch + night {(torchUp + nightUp) / noonUp:P1}");
        Assert.True(torchUp >= 0.4f * noonUp, $"torch at 2 m gives {torchUp}, under 40% of noon {noonUp}");

        // The mirror itself: full strength at the light, nothing at or past the radius.
        Assert.Equal(torchColor, PointLight(Vector3.UnitY, torch - new Vector3(0f, 0.02f, 0f), torch, TorchRadius, torchColor), new Vector3Comparer(1e-3f));
        Assert.Equal(Vector3.Zero, PointLight(Vector3.UnitY, torch - new Vector3(0f, TorchRadius + 0.01f, 0f), torch, TorchRadius, torchColor));
    }

    // CPU mirror of dreamPointLights in pointLights.glsl for one light: windowed (1 - d^2/r^2)^2 falloff
    // and a 0.25 wrap on the Lambert term.
    private static Vector3 PointLight(Vector3 normal, Vector3 fragPos, Vector3 position, float radius, Vector3 color)
    {
        Vector3 n = Vector3.Normalize(normal);
        Vector3 toLight = position - fragPos;
        float d2 = Vector3.Dot(toLight, toLight);
        float f = Math.Clamp(1f - d2 / (radius * radius), 0f, 1f);
        f *= f;
        float lambert = MathF.Max(Vector3.Dot(n, toLight / MathF.Sqrt(MathF.Max(d2, 1e-4f))), 0f);
        return color * (f * (0.25f + 0.75f * lambert));
    }

    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) =>
            MathF.Abs(a.X - b.X) <= tolerance && MathF.Abs(a.Y - b.Y) <= tolerance && MathF.Abs(a.Z - b.Z) <= tolerance;

        public int GetHashCode(Vector3 v) => 0;
    }

    private static float TerrainLuminance(FrameLighting lighting, Vector3 fragPos)
    {
        FrameLighting world = FrameLightingBuffer.ForWorld(in lighting, Eye);
        float sum = 0f;
        foreach (Vector3 normal in FaceNormals)
        {
            sum += Luminance(Saturate(Cube(in world, normal, fragPos, Grass)));
        }

        return sum / FaceNormals.Length;
    }

    // CPU mirror of cubeFragment.glsl over lighting.glsl.
    private static Vector3 Cube(in FrameLighting l, Vector3 normal, Vector3 fragPos, Vector3 baseColor)
    {
        Vector3 n = Vector3.Normalize(normal);
        Vector3 lit = (Ambient(in l, n) + Direct(in l, n)) * baseColor * l.Params.X;
        return ApplyFog(in l, lit, fragPos);
    }

    // CPU mirror of enemyFragment.glsl over lighting.glsl.
    private static Vector3 Enemy(in FrameLighting l, Vector3 normal, Vector3 fragPos, Vector3 baseColor)
    {
        Vector3 n = Vector3.Normalize(normal);
        Vector3 lit = (Ambient(in l, n) * l.Params.Z + Direct(in l, n) * 0.75f) * baseColor;
        lit = Vector3.ComponentMax(lit, baseColor * l.Params.Y);
        return ApplyFog(in l, lit * l.Params.X, fragPos);
    }

    private static Vector3 Ambient(in FrameLighting l, Vector3 n) =>
        Vector3.Lerp(l.AmbientGround.Xyz, l.AmbientSky.Xyz, n.Y * 0.5f + 0.5f);

    private static Vector3 Direct(in FrameLighting l, Vector3 n) =>
        MathF.Max(Vector3.Dot(n, l.SunDirection.Xyz), 0f) * l.SunDirection.W * l.SunColor.Xyz
        + MathF.Max(Vector3.Dot(n, l.MoonDirection.Xyz), 0f) * l.MoonDirection.W * l.MoonColor.Xyz;

    private static Vector3 ApplyFog(in FrameLighting l, Vector3 c, Vector3 fragPos)
    {
        float d = MathF.Max(Vector3.Distance(fragPos, l.CameraPosition.Xyz) - l.CameraPosition.W, 0f);
        float k = l.FogColor.W * d;
        return Vector3.Lerp(l.FogColor.Xyz, c, Math.Clamp(MathF.Pow(2f, -k * k), 0f, 1f));
    }

    private static Vector3 Saturate(Vector3 c) => Vector3.Clamp(c, Vector3.Zero, Vector3.One);

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private static void AssertFinite(FrameLighting l, float t)
    {
        foreach (Vector4 v in new[] { l.SunDirection, l.SunColor, l.MoonDirection, l.MoonColor, l.AmbientSky, l.AmbientGround, l.FogColor, l.CameraPosition, l.Params })
        {
            Assert.True(
                float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W),
                $"non-finite {v} at {t}");
        }
    }

    private static void AssertInUnitRange(Vector3 c, string what) =>
        Assert.True(c.X is >= 0f and <= 1f && c.Y is >= 0f and <= 1f && c.Z is >= 0f and <= 1f, $"{what} is {c}");
}
