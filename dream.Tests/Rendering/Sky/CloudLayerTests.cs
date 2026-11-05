using System.Globalization;
using System.Text.RegularExpressions;
using Dream.Rendering.Sky;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Sky;

public class CloudLayerTests
{
    private static readonly Vector4 Authored = new(0.35f, 0.35f, 0.55f, 0.9f);

    [Fact]
    public void The_Csharp_Constants_Are_The_Glsl_Constants()
    {
        string glsl = File.ReadAllText(Path.Combine(ShaderDirectory(), "clouds.glsl"));
        Assert.Equal(CloudLayer.PlaneY, Const(glsl, "CloudPlaneY"));
        Assert.Equal(CloudLayer.CellsPerUnit, Const(glsl, "CloudCellsPerUnit"));
        Assert.Equal(CloudLayer.Lacunarity, Const(glsl, "CloudLacunarity"));
        Assert.Equal(CloudLayer.Gain, Const(glsl, "CloudGain"));
        Assert.Equal(CloudLayer.AmplitudeSum, Const(glsl, "CloudAmplitudeSum"));
        Assert.Equal(CloudLayer.Contrast, Const(glsl, "CloudContrast"));
        Assert.Equal(CloudLayer.SoftnessScale, Const(glsl, "CloudSoftnessScale"));
        Match dir = Regex.Match(glsl, @"const vec2 CloudDirection = vec2\(([\d.]+), ([\d.]+)\)");
        Assert.True(dir.Success);
        Assert.Equal(CloudLayer.Direction.X, float.Parse(dir.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(CloudLayer.Direction.Y, float.Parse(dir.Groups[2].Value, CultureInfo.InvariantCulture));
        Assert.Equal(1f, CloudLayer.Direction.Length, 5);
        // 4 octaves, the sum of the geometric amplitudes.
        Assert.Contains("i < 4", glsl);
        Assert.Equal(1f + 0.5f + 0.25f + 0.125f, CloudLayer.AmplitudeSum);
        Assert.Contains("vec2(17.0, 31.0)", glsl);
        Assert.Contains("fract(vec3(p.xyx) * 0.1031)", glsl);
        Assert.Contains("p3.yzx + 33.33", glsl);
    }

    [Fact]
    public void Glsl_Has_No_Include_Or_Version_And_One_Cover_Function()
    {
        string glsl = File.ReadAllText(Path.Combine(ShaderDirectory(), "clouds.glsl"));
        Assert.DoesNotMatch(new Regex(@"^\s*#\s*(include|version)\b", RegexOptions.Multiline), glsl);
        Assert.Single(Regex.Matches(glsl, @"float dreamCloudCover\(vec2 planeXZ\)"));
    }

    [Fact]
    public void Coverage_Zero_Is_Clear_And_Coverage_One_Is_Solid_Everywhere()
    {
        Random random = new(7);
        for (int i = 0; i < 1000; i++)
        {
            Vector2 p = new((float)random.NextDouble() * 2000f - 1000f, (float)random.NextDouble() * 2000f - 1000f);
            float t = (float)random.NextDouble() * 4000f;
            Assert.Equal(0f, CloudLayer.Cover(p, new Vector4(0f, 0.35f, 0.55f, 0.9f), t));
            Assert.Equal(1f, CloudLayer.Cover(p, new Vector4(1f, 0.35f, 0.55f, 0.9f), t));
        }
    }

    // The clouds never wrap the clock. The drift is reduced modulo the lattice period (in double precision, on
    // the CPU), which is seamless only because the lattice hash wraps at that same period at every octave.
    [Fact]
    public void The_Field_Repeats_Every_PeriodCells_On_Both_Axes()
    {
        Random random = new(5);
        for (int i = 0; i < 2000; i++)
        {
            Vector2 p = new((float)random.NextDouble() * 4000f, (float)random.NextDouble() * 4000f);
            Vector2 offset = new((float)random.NextDouble() * CloudLayer.PeriodCells, (float)random.NextDouble() * CloudLayer.PeriodCells);
            float a = CloudLayer.CoverAtOffset(p, Authored, offset);
            Assert.Equal(a, CloudLayer.CoverAtOffset(p, Authored, offset + new Vector2(CloudLayer.PeriodCells, 0f)), 0.002f);
            Assert.Equal(a, CloudLayer.CoverAtOffset(p, Authored, offset - new Vector2(0f, CloudLayer.PeriodCells)), 0.002f);
        }
    }

    // In the style of BoundaryMistFieldTests: the cover is continuous across every place the old pipeline
    // snapped (the mist's 6400 s wrap) and across where the reduced drift itself wraps on each axis.
    [Theory]
    [InlineData(6400d)]
    [InlineData(12800d)]
    [InlineData(11111.111111111111d)]   // x drift reaches PeriodCells: 64 / (0.9 * 0.8 * 0.008)
    [InlineData(14814.814814814814d)]   // y drift reaches PeriodCells: 64 / (0.9 * 0.6 * 0.008)
    [InlineData(22222.222222222222d)]
    [InlineData(1e7d)]
    public void The_Cover_Is_Continuous_Across_The_Wrap(double boundarySeconds)
    {
        const double epsilon = 1e-3;
        Random random = new(23);
        float worst = 0f;
        for (int i = 0; i < 1500; i++)
        {
            Vector2 p = new((float)random.NextDouble() * 1500f, (float)random.NextDouble() * 1500f);
            float before = CloudLayer.Cover(p, Authored, boundarySeconds - epsilon);
            float after = CloudLayer.Cover(p, Authored, boundarySeconds + epsilon);
            worst = MathF.Max(worst, MathF.Abs(before - after));
        }

        Assert.True(worst <= 0.02f, $"cover jumped by {worst} across t = {boundarySeconds}");
    }

    [Fact]
    public void The_Drift_Is_Reduced_Into_One_Period_Whatever_The_Clock()
    {
        foreach (double seconds in new[] { 0d, 1d, 6400d, 11111.2d, 1e6d, 1e9d })
        {
            Vector2 cells = CloudLayer.ScrollCells(0.9f, seconds);
            Assert.InRange(cells.X, 0f, CloudLayer.PeriodCells);
            Assert.InRange(cells.Y, 0f, CloudLayer.PeriodCells);
        }

        Assert.Equal(Vector2.Zero, CloudLayer.ScrollCells(0f, 12345d));
    }

    [Fact]
    public void The_Glsl_Wraps_The_Lattice_At_The_Period_And_Takes_The_Drift_From_FrameTime_Yz()
    {
        string glsl = File.ReadAllText(Path.Combine(ShaderDirectory(), "clouds.glsl"));
        Assert.Equal(CloudLayer.PeriodCells, Const(glsl, "CloudPeriodCells"));
        Assert.Equal(4, Regex.Matches(glsl, @"dreamCloudHash\(mod\(cell[^;]*CloudPeriodCells\)\)").Count);
        Assert.Contains("planeXZ * CloudCellsPerUnit - uFrameTime.yz", glsl);
        // No clock in the cover: wrapping a clock is what made the sheet pop.
        Assert.DoesNotContain("uFrameTime.x", Regex.Replace(glsl, @"//[^\n]*", ""));
        // Every octave's stretch divides the period, and the per-octave shift is whole cells.
        float stretch = MathF.Pow(CloudLayer.Lacunarity, CloudLayer.Octaves - 1);
        Assert.Equal(0f, CloudLayer.PeriodCells % stretch);
        Assert.Equal(0f, 17f % 1f);
    }

    // The sheet is traced from the camera's real position; uCameraPosition is the fog origin (the isometric focus).
    [Fact]
    public void The_Sky_Traces_The_Sheet_From_The_Real_Eye_Not_The_Fog_Origin()
    {
        string sky = File.ReadAllText(Path.Combine(ShaderDirectory(), "skyFragment.glsl"));
        Assert.Contains("vec3 eye = uEyePosition;", sky);
        Assert.DoesNotContain("vec3 eye = uCameraPosition", sky);
        Assert.Contains("uEyePosition", Dream.Rendering.Sky.SkyRenderer.UniformNames);
    }

    [Fact]
    public void The_Field_Is_Continuous()
    {
        Random random = new(11);
        float worst = 0f;
        for (int i = 0; i < 4000; i++)
        {
            Vector2 p = new((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);
            double angle = random.NextDouble() * Math.PI * 2;
            Vector2 q = p + 0.5f * new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            worst = MathF.Max(worst, MathF.Abs(CloudLayer.Cover(p, Authored, 3f) - CloudLayer.Cover(q, Authored, 3f)));
        }

        Assert.True(worst <= 0.08f, $"largest step between neighbours 0.5 units apart: {worst}");
    }

    [Fact]
    public void The_Authored_Coverage_Is_Partly_Cloudy_And_Drifts_With_The_Clock()
    {
        int covered = 0;
        int differing = 0;
        const int samples = 4000;
        for (int i = 0; i < samples; i++)
        {
            Vector2 p = new(i % 63 * 13f, i / 63 * 13f);
            float a = CloudLayer.Cover(p, Authored, 0f);
            if (a > 0.5f)
            {
                covered++;
            }

            if (MathF.Abs(a - CloudLayer.Cover(p, Authored, 60f)) > 0.01f)
            {
                differing++;
            }
        }

        Assert.InRange(covered / (float)samples, 0.15f, 0.65f);
        Assert.True(differing > samples / 20, "the field must move with the clock");
    }

    [Fact]
    public void The_Sun_Mask_Is_One_Without_Cover_And_Never_Below_One_Minus_Strength()
    {
        Vector3 sun = Vector3.Normalize(new Vector3(-0.3f, 0.86f, -0.4f));
        Assert.Equal(1f, CloudLayer.SunMask(new Vector3(5f, 1f, 5f), sun, Vector4.Zero, 10f));
        Random random = new(3);
        float min = 1f;
        for (int i = 0; i < 2000; i++)
        {
            Vector3 p = new((float)random.NextDouble() * 500f, 1f, (float)random.NextDouble() * 500f);
            float m = CloudLayer.SunMask(p, sun, Authored, 5f);
            Assert.InRange(m, 1f - Authored.Z - 1e-5f, 1f);
            min = MathF.Min(min, m);
        }

        Assert.True(min < 0.9f, "some ground must be in shadow");
    }

    private static float Const(string glsl, string name)
    {
        Match m = Regex.Match(glsl, @"const float " + name + @" = ([\d.]+);");
        Assert.True(m.Success, name);
        return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string ShaderDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "dream", "shaders");
    }
}
