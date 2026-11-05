using System.Reflection;
using Dream.Rendering.Core;
using Dream.Rendering.Shadows;
using Dream.Rendering.Sky;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

public class ShadowSettingsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("garbage", true)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    public void Shadows_Are_On_By_Default_And_Only_Zero_Turns_Them_Off(string? value, bool expected)
    {
        Assert.Equal(expected, ShadowSettings.ParseEnabled(value));
    }

    [Theory]
    [InlineData(-5, 512)]
    [InlineData(0, 512)]
    [InlineData(512, 512)]
    [InlineData(700, 512)]
    [InlineData(768, 512)]   // a tie rounds down, exactly like MsaaSettings.Sanitize
    [InlineData(769, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1500, 1024)]
    [InlineData(2048, 2048)]
    [InlineData(3000, 2048)]
    [InlineData(3072, 2048)]
    [InlineData(3073, 4096)]
    [InlineData(100000, 4096)]
    public void The_Sun_Resolution_Snaps_To_The_Nearest_Allowed_Value(int requested, int expected)
    {
        Assert.Equal(expected, ShadowSettings.SanitizeSunResolution(requested));
    }

    [Fact]
    public void The_Resolution_Sanitiser_Is_Nearest_Candidate_With_The_Lowest_Winning_A_Tie()
    {
        foreach (int value in new[] { -1, 0, 1, 255, 256, 257, 768, 769, 3072, 3073, 9999 })
        {
            int nearest = ShadowSettings.AllowedSunResolutions.OrderBy(c => Math.Abs(c - value)).First();
            Assert.Equal(nearest, ShadowSettings.SanitizeSunResolution(value));
        }
    }

    [Theory]
    [InlineData(null, 2048)]
    [InlineData("", 2048)]
    [InlineData("nope", 2048)]
    [InlineData("1024", 1024)]
    [InlineData(" 4096 ", 4096)]
    [InlineData("1000", 1024)]
    [InlineData("1", 512)]
    public void The_Resolution_Knob_Defaults_To_2048_And_Sanitises(string? value, int expected)
    {
        Assert.Equal(expected, ShadowSettings.ResolveSunResolution(value));
        Assert.Equal(2048, ShadowSettings.DefaultSunResolution);
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData("", 4)]
    [InlineData("junk", 4)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    [InlineData("9", 4)]
    [InlineData("-3", 0)]
    public void The_Casting_Fire_Count_Defaults_To_Four_And_Clamps_To_Zero_Through_Four(string? value, int expected)
    {
        Assert.Equal(expected, ShadowSettings.ResolveCastingFireCount(value));
    }

    [Fact]
    public void The_Default_Casting_Fire_Count_Is_Four_By_Explicit_Decision_Not_The_Designs_Two()
    {
        Assert.Equal(4, ShadowSettings.DefaultCastingFireCount);
        Assert.Equal(ShadowSettings.MaxCastingFireCount, ShadowSettings.DefaultCastingFireCount);
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Shadows", "ShadowSettings.cs"));
        Assert.Contains("EXPLICIT DECISION", source);
        Assert.Contains("FIRST knob to turn down", source);
    }

    [Fact]
    public void The_Environment_Variables_Are_The_House_Names()
    {
        Assert.Equal("DREAM_SHADOWS", ShadowSettings.EnabledEnvironmentVariable);
        Assert.Equal("DREAM_SHADOW_RESOLUTION", ShadowSettings.ResolutionEnvironmentVariable);
        Assert.Equal("DREAM_SHADOW_FIRES", ShadowSettings.FiresEnvironmentVariable);
    }

    [Fact]
    public void The_Ortho_Side_Is_The_Diagonal_Of_Receiver_Side_And_Height()
    {
        float side = ShadowSettings.ReceiverSide;
        float height = ShadowSettings.ReceiverHeight;
        Assert.Equal(MathF.Sqrt(side * side + height * height), ShadowSettings.OrthoSide, 5);
    }

    [Fact]
    public void The_Ortho_Side_Maximises_The_Light_Space_Extent_Of_The_Receiver_Box_Over_Elevation()
    {
        // The extent S cos(t) + H sin(t) peaks at sqrt(S^2 + H^2); scan t to prove the closed form is the maximum.
        float best = 0f;
        for (int i = 0; i <= 9000; i++)
        {
            float t = MathHelper.DegreesToRadians(i * 0.01f);
            best = MathF.Max(best, ShadowSettings.ReceiverSide * MathF.Cos(t) + ShadowSettings.ReceiverHeight * MathF.Sin(t));
        }

        Assert.Equal(ShadowSettings.OrthoSide, best, 2);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void The_World_Texel_Is_The_Ortho_Side_Over_The_Resolution(int resolution)
    {
        Assert.Equal(ShadowSettings.OrthoSide / resolution, ShadowSettings.TexelWorldFor(resolution), 7);
        Assert.Equal(ShadowSettings.OrthoSide, ShadowSettings.TexelWorldFor(resolution) * resolution, 3);
    }

    [Fact]
    public void The_Atlas_Is_A_Four_By_Four_Grid_Filled_Exactly_By_Four_Lights_Of_Four_Faces()
    {
        Assert.Equal(ShadowSettings.GridSide * ShadowSettings.GridSide, ShadowSettings.MaxCastingFireCount * ShadowSettings.FacesPerLight);
        Assert.Equal(ShadowSettings.AtlasResolution, ShadowSettings.TileSide * ShadowSettings.GridSide);
        Assert.Equal(0, ShadowSettings.AtlasResolution % ShadowSettings.GridSide);
    }

    [Fact]
    public void The_Face_Fov_Widens_Ninety_Degrees_Exactly_Enough_For_The_Gutter()
    {
        float expected = 2f * MathF.Atan(MathF.Tan(MathF.PI / 4f) * ShadowSettings.TileSide / (ShadowSettings.TileSide - 2f * ShadowSettings.TileGutter));
        Assert.Equal(expected, ShadowSettings.FaceFovRadians, 6);
        Assert.True(ShadowSettings.FaceFovRadians > MathF.PI / 2f);
        // The 90 degree cone's edge (tan 45 = 1) must land exactly on the inner tile edge, one gutter inside the tile.
        float edgeFraction = 1f / MathF.Tan(ShadowSettings.FaceFovRadians / 2f);
        Assert.Equal((ShadowSettings.TileSide - 2f * ShadowSettings.TileGutter) / ShadowSettings.TileSide, edgeFraction, 4);
    }

    [Fact]
    public void The_Caster_Extension_Is_Receiver_Height_Over_Sine_Elevation_Floored_And_Capped()
    {
        Assert.Equal(ShadowSettings.ReceiverHeight / 0.8f, ShadowSettings.CasterExtensionFor(0.8f), 4);
        Assert.Equal(ShadowSettings.ReceiverHeight, ShadowSettings.CasterExtensionFor(1f), 4);
        Assert.Equal(ShadowSettings.CasterExtensionFor(ShadowSettings.MinElevationSin), ShadowSettings.CasterExtensionFor(0.001f));
        Assert.Equal(ShadowSettings.CasterExtensionFor(ShadowSettings.MinElevationSin), ShadowSettings.CasterExtensionFor(-0.5f));
        Assert.True(ShadowSettings.CasterExtensionFor(0.3f) > ShadowSettings.CasterExtensionFor(0.6f));
        Assert.Equal(2f * WorldVisibility.MaxRenderDistance, ShadowSettings.MaxCasterExtension);
        Assert.True(ShadowSettings.CasterExtensionFor(ShadowSettings.MinElevationSin) <= ShadowSettings.MaxCasterExtension);
    }

    [Fact]
    public void The_Elevation_Floor_Equals_The_Sky_Models_Light_Fade_Top()
    {
        FieldInfo field = typeof(SkyModel).GetField("LightFadeAbove", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SkyModel.LightFadeAbove moved; repoint this relation.");
        Assert.Equal((float)field.GetRawConstantValue()!, ShadowSettings.MinElevationSin);
    }

    [Fact]
    public void The_Bias_Constants_Are_Related_Not_Restated()
    {
        float defaultTexel = ShadowSettings.TexelWorldFor(ShadowSettings.DefaultSunResolution);
        // Calibrated: at the default resolution the texel-scaled slope bias IS the design's world-unit slope bias.
        Assert.Equal(ShadowSettings.SlopeBiasWorld, ShadowSettings.SlopeBiasWorldFor(defaultTexel), 6);
        Assert.Equal(ShadowSettings.SlopeBiasWorld * ShadowSettings.SlopeTanMax, ShadowSettings.SlopeBiasMaxWorldFor(defaultTexel), 6);
        Assert.Equal(0.080f, ShadowSettings.SlopeBiasMaxWorldFor(defaultTexel), 5);
        // Linear in the texel beyond it: a quarter of the resolution has four times the world bias.
        Assert.Equal(4f * ShadowSettings.SlopeBiasWorld, ShadowSettings.SlopeBiasWorldFor(ShadowSettings.TexelWorldFor(512)), 6);
        // The UBO-side coefficient: slope / constant = SlopePerInverseResolution * (1 / resolution).
        foreach (int resolution in ShadowSettings.AllowedSunResolutions)
        {
            float texel = ShadowSettings.TexelWorldFor(resolution);
            Assert.Equal(
                ShadowSettings.SlopeBiasWorldFor(texel) / ShadowSettings.ConstantBiasWorld,
                ShadowSettings.SlopePerInverseResolution / resolution,
                3);
        }
    }

    [Fact]
    public void The_Sampler_Units_Are_5_And_6_And_Collide_With_No_Other_Sampler()
    {
        Assert.Equal(5, ShadowSettings.SunTextureUnit);
        Assert.Equal(6, ShadowSettings.AtlasTextureUnit);
        int[] taken = [0, 1, 2, 3, 4, Render.TerrainTilesUnit, Render.TerrainMaterialMapUnit];
        Assert.DoesNotContain(ShadowSettings.SunTextureUnit, taken);
        Assert.DoesNotContain(ShadowSettings.AtlasTextureUnit, taken);
        Assert.NotEqual(ShadowSettings.SunTextureUnit, ShadowSettings.AtlasTextureUnit);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
