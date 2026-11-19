using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The shadow map is rendered from the dominant light (the sun, else the moon). Its visibility must therefore dim THAT light's
// term: at night the sun term is 0, so a visibility on the sun term alone shadowed nothing. These tests pin the rule in the two
// shaders that apply it, its agreement with Render.BuildSunFrame, and that the pin can fail.
public class MoonShadowTermTests
{
    private static string Shader(string name) => FireShadowSamplingMirrorTests.Read("dream", "shaders", name).Replace("\r\n", "\n");

    private static string Code(string text) => Regex.Replace(text, @"//[^\n]*", string.Empty);

    // The factor a directional term carries, for a given dominant light: finds `(sunMap ? a : b)` on the term's text and
    // returns a when the map is the sun's, b otherwise. Null when the term carries no select at all.
    private static string? FactorOn(string termText, bool mapIsSun)
    {
        Match select = Regex.Match(termText, @"\(sunMap \? (?<a>[\w.]+) : (?<b>[\w.]+)\)");
        return select.Success ? select.Groups[mapIsSun ? "a" : "b"].Value : null;
    }

    // The sun and moon terms of dreamDirectShaded's return statement, as the shader forms them.
    private static (string Sun, string Moon) CloudTerms(string clouds)
    {
        string code = Code(clouds);
        string body = code[code.IndexOf("vec3 dreamDirectShaded", StringComparison.Ordinal)..];
        int ret = body.IndexOf("return ", StringComparison.Ordinal);
        string statement = body[ret..body.IndexOf(';', ret)];
        string[] terms = statement.Split("\n         + ");
        Assert.Equal(2, terms.Length);
        return (terms[0], terms[1]);
    }

    // The sun and moon lobes of the equipment specular in enemyFragment.glsl.
    private static (string Sun, string Moon) SpecularTerms(string enemy)
    {
        string code = Code(enemy);
        int sun = code.IndexOf("vec3 spec = dreamEquipLobe(n, v, uSunDirection.xyz", StringComparison.Ordinal);
        string sunStatement = code[sun..code.IndexOf(';', sun)];
        int moon = code.IndexOf("spec += dreamEquipLobe(n, v, uMoonDirection.xyz", StringComparison.Ordinal);
        string moonStatement = code[moon..code.IndexOf(';', moon)];
        return (sunStatement, moonStatement);
    }

    // Empty when the pair obeys the rule: the visibility variable on the sun term exactly when the map is the sun's, on the moon
    // term exactly when it is not, and a literal 1.0 on the other one.
    private static List<string> Violations((string Sun, string Moon) terms, string visibility)
    {
        List<string> problems = [];
        foreach (bool mapIsSun in new[] { true, false })
        {
            string? sun = FactorOn(terms.Sun, mapIsSun);
            string? moon = FactorOn(terms.Moon, mapIsSun);
            if (sun != (mapIsSun ? visibility : "1.0"))
            {
                problems.Add($"sun term carries {sun ?? "nothing"} when the map is the {(mapIsSun ? "sun's" : "moon's")}");
            }

            if (moon != (mapIsSun ? "1.0" : visibility))
            {
                problems.Add($"moon term carries {moon ?? "nothing"} when the map is the {(mapIsSun ? "sun's" : "moon's")}");
            }
        }

        return problems;
    }

    [Fact]
    public void Clouds_Applies_The_Visibility_To_The_Moon_Term_Exactly_When_The_Map_Is_Not_The_Suns()
    {
        string clouds = Shader("clouds.glsl");
        Assert.Empty(Violations(CloudTerms(clouds), "v"));
        string body = Code(clouds);
        Assert.Contains("float v = dreamSunVisibility(fragPos, n);", body);
        Assert.Contains("bool sunMap = dreamShadowMapIsSun();", body);

        // The cloud mask stays on the sun term only.
        (string sun, string moon) = CloudTerms(clouds);
        Assert.Contains("sunMask", sun);
        Assert.DoesNotContain("sunMask", moon);
    }

    [Fact]
    public void The_Equipment_Specular_Follows_The_Same_Rule()
    {
        string enemy = Shader("enemyFragment.glsl");
        Assert.Empty(Violations(SpecularTerms(enemy), "mapV"));
        string body = Code(enemy);
        Assert.Contains("float mapV = dreamSunVisibility(fragPos, n);", body);
        Assert.Contains("bool sunMap = dreamShadowMapIsSun();", body);
    }

    [Fact]
    public void A_Shader_That_Drops_Or_Swaps_A_Factor_Fails_The_Check()
    {
        string cloudsWithoutMoon = Shader("clouds.glsl").Replace(" * (sunMap ? 1.0 : v)", string.Empty);
        Assert.NotEmpty(Violations(CloudTerms(cloudsWithoutMoon), "v"));

        string enemyWithoutMoon = Shader("enemyFragment.glsl").Replace(" * (sunMap ? 1.0 : mapV)", string.Empty);
        Assert.NotEmpty(Violations(SpecularTerms(enemyWithoutMoon), "mapV"));

        string cloudsWithoutSun = Shader("clouds.glsl").Replace(" * (sunMap ? v : 1.0)", string.Empty);
        Assert.NotEmpty(Violations(CloudTerms(cloudsWithoutSun), "v"));

        string swapped = Shader("clouds.glsl").Replace("(sunMap ? v : 1.0)", "(sunMap ? 1.0 : v)");
        Assert.NotEmpty(Violations(CloudTerms(swapped), "v"));
    }

    [Fact]
    public void The_Shader_Rule_Is_The_Rule_Render_BuildSunFrame_Uses()
    {
        string lighting = Code(Shader("lighting.glsl"));
        Assert.Contains("bool dreamShadowMapIsSun() { return uSunDirection.w >= uMoonDirection.w; }", lighting);
        // dreamSunVisibility's own toLight choice is the same comparison.
        Assert.Contains("uSunDirection.w >= uMoonDirection.w ? uSunDirection.xyz : uMoonDirection.xyz", lighting);

        static bool GlslRule(Vector4 sun, Vector4 moon) => sun.W >= moon.W;

        Vector3 sunDir = Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f));
        Vector3 moonDir = Vector3.Normalize(new Vector3(-0.5f, 0.6f, 0.1f));
        (float SunW, float MoonW)[] cases = [(1f, 0.2f), (0f, 0.4f), (0.3f, 0.3f), (0.5f, 0f)];
        foreach ((float sunW, float moonW) in cases)
        {
            FrameLighting lighting2 = FrameLighting.ReferenceNoon;
            lighting2.SunDirection = new Vector4(sunDir, sunW);
            lighting2.MoonDirection = new Vector4(moonDir, moonW);

            ShadowFrame built = Render.BuildSunFrame(in lighting2, Vector3.Zero, 1f, 2048);
            Vector3 light = GlslRule(lighting2.SunDirection, lighting2.MoonDirection) ? sunDir : moonDir;
            ShadowFrame expected = ShadowFrame.ForSun(Vector3.Zero, light, light.Y, 2048, 1f);
            Assert.Equal(expected.SunLightViewProjection, built.SunLightViewProjection);
        }

        // Equal intensities: the sun is the map's light (>=), in both the C# and the GLSL rule.
        Assert.True(GlslRule(new Vector4(sunDir, 0.3f), new Vector4(moonDir, 0.3f)));
    }
}
