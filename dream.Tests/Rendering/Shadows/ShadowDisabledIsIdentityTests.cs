using System.Text.RegularExpressions;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// DREAM_SHADOWS=0 must reproduce the pre-shadow frame. The argument, in four steps, each pinned below:
//   1. With shadows off the C# side hands the shaders strength 0 (sun) and count 0 (fires): ShadowFrame.None.
//   2. With those, dreamSunVisibility and dreamFireVisibility return a literal 1.0 BEFORE any texture fetch.
//   3. Strip the two new multiplicands from the shader text and what is left is, character for character, the pre-shadow
//      expression (embedded below as it was on master before the wave).
//   4. x * 1.0 == x exactly in IEEE-754, so the lighting sums are bitwise the pre-shadow ones.
// What this cannot show: that a given GPU driver compiles `* 1.0` to a no-op (every one does: the factor is a constant-returning
// call after inlining), and that the old ground blobs come back (workstream D2 makes them conditional on !ShadowSettings.Enabled).
public class ShadowDisabledIsIdentityTests
{
    // The pre-shadow text, as master had it.
    private const string PreShadowSunLine =
        "    return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb * sunMask\n" +
        "         + max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb;";

    private const string PreShadowPointLightLine =
        "        sum += uPointLightColor[i].rgb * (f * (0.25 + 0.75 * lambert)); // 0.25 wrap keeps faces readable";

    private static string Shader(string name) => FireShadowSamplingMirrorTests.Read("dream", "shaders", name).Replace("\r\n", "\n");

    private static string FunctionBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[(open + 1)..i];
            }
        }

        throw new InvalidOperationException("Unbalanced braces after " + signature);
    }

    // ---- 1. the C# side ----

    [Fact]
    public void Shadows_Off_Hands_The_Shaders_Strength_Zero_And_No_Fires()
    {
        Assert.False(ShadowSettings.ParseEnabled("0"));
        Assert.False(ShadowSettings.ParseEnabled(" 0 "));
        Assert.True(ShadowSettings.ParseEnabled(null));

        // The disabled path is ShadowFrame.None: written into the frame lighting it leaves strength 0, and its fire block is empty.
        FrameLighting lighting = FrameLighting.ReferenceNoon;
        ShadowFrame.None.WriteSunTo(ref lighting);
        Assert.Equal(0f, lighting.SunShadowParams.W);
        Assert.Equal(0, FireShadowList.FromFrame(ShadowFrame.None).Count);
    }

    [Fact]
    public void The_View_Model_Range_Carries_No_Shadows_At_All_Even_When_The_World_Range_Does()
    {
        ShadowFrame frame = ShadowFrame.ForSun(Vector3.Zero, Vector3.Normalize(new Vector3(1f, 2f, 1f)), 0.8f, 2048, 1f);
        FrameLighting world = FrameLighting.ReferenceNoon;
        frame.WriteSunTo(ref world);
        Assert.Equal(1f, world.SunShadowParams.W);

        FrameLighting viewModel = FrameLightingBuffer.ForViewModel(in world, Matrix4.Identity);

        Assert.Equal(0f, viewModel.SunShadowParams.W);
        Assert.Equal(Matrix4.Zero, viewModel.SunShadowMatrix);
    }

    // ---- 2. a literal 1.0 with no fetch ----

    [Fact]
    public void The_Sun_Visibility_Returns_One_Before_Any_Texture_Fetch_Whenever_Strength_Is_Zero()
    {
        string body = FunctionBody(Shader("lighting.glsl"), "float dreamSunVisibility(vec3 fragPos, vec3 n)");
        string live = body[(body.IndexOf("#if DREAM_SUN_SHADOW_LIVE", StringComparison.Ordinal))..body.IndexOf("#else", StringComparison.Ordinal)];

        int guard = live.IndexOf("if (uSunShadowParams.w <= 0.0) { return 1.0; }", StringComparison.Ordinal);
        int fetch = live.IndexOf("texture(", StringComparison.Ordinal);
        Assert.True(guard >= 0, "The strength guard is missing.");
        Assert.True(fetch > guard, "A texture fetch precedes the strength guard.");
        // Nothing before the guard touches the map or the matrix either.
        string beforeGuard = live[..guard];
        Assert.DoesNotContain("uSunShadowMap", beforeGuard);
        Assert.DoesNotContain("dreamSunShadowMatrix", beforeGuard);

        // And the build with the switch at 0 (what the compiler sees) is a bare `return 1.0;`.
        string inert = body[(body.IndexOf("#else", StringComparison.Ordinal) + "#else".Length)..body.IndexOf("#endif", StringComparison.Ordinal)];
        Assert.Equal("return 1.0;", inert.Trim());
    }

    [Fact]
    public void The_Fire_Visibility_Returns_One_Before_Any_Texture_Fetch_When_No_Fire_Casts_Or_The_Light_Is_Not_One()
    {
        string body = FunctionBody(Shader("fireShadows.glsl"), "float dreamFireVisibility(int light, vec3 fragPos, vec3 n)").Trim();

        // The very first statement is the count guard.
        Assert.StartsWith("if (uFireShadowCount.x <= 0) { return 1.0; }", body);
        int firstFetch = body.IndexOf("texture", StringComparison.Ordinal);
        Assert.True(firstFetch > 0);
        string beforeFetch = body[..firstFetch];
        Assert.Contains("if (slot < 0) { return 1.0; }", beforeFetch);
        Assert.Contains("if (range.w <= 0.0) { return 1.0; }", beforeFetch);
        Assert.Contains("if (a0.y >= max(a0.x, a0.z)) { return 1.0; }", beforeFetch);
        Assert.DoesNotContain("uFireShadowAtlas", beforeFetch.Replace("textureSize(uFireShadowAtlas", string.Empty));
    }

    [Fact]
    public void The_Point_Light_Loop_Does_Not_Evaluate_The_Visibility_For_A_Light_That_Contributes_Nothing()
    {
        string source = Shader("pointLights.glsl");
        Assert.Contains("float visibility = f > 0.0 ? dreamFireVisibility(i, fragPos, n) : 1.0;", source);
    }

    // ---- 3. strip the multiplicands and the pre-shadow text is left ----

    [Fact]
    public void Without_The_Sun_Visibility_Factor_Dream_Direct_Shaded_Is_The_Pre_Shadow_Expression_Character_For_Character()
    {
        string clouds = Shader("clouds.glsl");
        string body = FunctionBody(clouds, "vec3 dreamDirectShaded(vec3 n, vec3 fragPos)");
        Assert.Single(Regex.Matches(body, @"dreamSunVisibility\(fragPos, n\)"));

        string stripped = body.Replace(" * dreamSunVisibility(fragPos, n)", string.Empty);
        Assert.Contains(PreShadowSunLine, stripped);
    }

    [Fact]
    public void Without_The_Fire_Visibility_Factor_The_Point_Light_Sum_Is_The_Pre_Shadow_Line_Character_For_Character()
    {
        string source = Shader("pointLights.glsl");
        string stripped = source
            .Replace("        float visibility = f > 0.0 ? dreamFireVisibility(i, fragPos, n) : 1.0;\n", string.Empty)
            .Replace(" * visibility;", ";");
        Assert.Contains(PreShadowPointLightLine, stripped);
    }

    [Fact]
    public void The_Lit_Expressions_Of_The_Two_World_Shaders_And_The_Direct_Contract_Are_Untouched()
    {
        string cube = Shader("cubeFragment.glsl");
        string enemy = Shader("enemyFragment.glsl");
        Assert.Contains("    vec3 lit = (dreamAmbient(n) + dreamDirectShaded(n, fragPos) + dreamPointLights(n, fragPos)) * baseColor.rgb;", cube);
        Assert.Contains("    vec3 lit = (dreamAmbient(n) * uLightingParams.z + (dreamDirectShaded(n, fragPos) + dreamPointLights(n, fragPos)) * ActorDirectScale) * baseColor.rgb;", enemy);

        string lighting = Shader("lighting.glsl");
        Assert.Contains(
            "vec3 dreamDirect(vec3 n) {\n" +
            "    return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb\n" +
            "         + max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb;\n" +
            "}\n",
            lighting);
    }

    // ---- the art instrument ----

    // tools/ui-assets/enginelook.py restates the actor transfer function and is what the A3/M3 art gates measure against. It was
    // stale once (it cited enemyFragment.glsl:66 after the call site had moved, and knew nothing of the shadows), so the shader lines
    // it restates are pinned on THIS side too, and `python tools/ui-assets/enginelook.py` pins them on the Python side.
    [Fact]
    public void The_Art_Instrument_Restates_The_Shader_Lines_It_Measures_Against_Character_For_Character()
    {
        string py = FireShadowSamplingMirrorTests.Read("tools", "ui-assets", "enginelook.py");

        (string Shader, string Line)[] contract =
        [
            ("clouds.glsl", "return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb * sunMask * dreamSunVisibility(fragPos, n)"),
            ("clouds.glsl", "+ max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb;"),
            ("lighting.glsl", "vec3 dreamAmbient(vec3 n) { return mix(uAmbientGround.rgb, uAmbientSky.rgb, n.y * 0.5 + 0.5); }"),
            ("enemyFragment.glsl", "const float ActorDirectScale = 0.75;"),
            ("enemyFragment.glsl", "lit = max(lit, baseColor.rgb * uLightingParams.y);"),
            ("pointLights.glsl", "sum += uPointLightColor[i].rgb * (f * (0.25 + 0.75 * lambert)) * visibility;"),
            ("pointLights.glsl", "return PointPeak * w * w / (1.0 + PointCore * s);"),
        ];
        foreach ((string shader, string line) in contract)
        {
            Assert.Contains(line, Shader(shader));
            Assert.Contains(line, py);
        }

        // The header states the visibility terms and cites symbols, not the line numbers that went stale.
        Assert.Contains("sunMask*sunV", py);
        Assert.Contains("lightV", py);
        Assert.Contains("def check_contract_against_shaders", py);
        Assert.DoesNotMatch(@"enemyFragment\.glsl:\d+", py);
        Assert.DoesNotMatch(@"lighting\.glsl:\d+", py);
        Assert.DoesNotMatch(@"pointLights\.glsl:\d+", py);
    }

    // ---- 4. x * 1.0 is x ----

    [Fact]
    public void Multiplying_By_The_Disabled_Visibility_Changes_No_Bit_Of_The_Lighting_Sum()
    {
        // The two expressions as the shaders form them, with the visibility the disabled path returns (mirror functions
        // with strength 0 / count 0, i.e. the same early outs the GLSL takes).
        Random random = new(99);
        FireShadowList empty = FireShadowList.FromFrame(ShadowFrame.None);
        FireShadowMirror.Atlas atlas = new(64);
        int compared = 0;
        for (int i = 0; i < 2000; i++)
        {
            Vector3 n = Vector3.Normalize(new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
            Vector3 sun = Vector3.Normalize(new Vector3((float)random.NextDouble(), (float)random.NextDouble() + 0.1f, (float)random.NextDouble()));
            float intensity = (float)random.NextDouble() * 1.4f;
            float colour = 0.3f + (float)random.NextDouble();
            float mask = 1f - 0.4f * (float)random.NextDouble();

            float before = MathF.Max(Vector3.Dot(n, sun), 0f) * intensity * colour * mask;
            float visibility = FireShadowMirror.Visibility(empty, atlas, i % 16, new Vector3(i, 0f, -i), n);
            float after = MathF.Max(Vector3.Dot(n, sun), 0f) * intensity * colour * mask * visibility;
            Assert.Equal(BitConverter.SingleToInt32Bits(before), BitConverter.SingleToInt32Bits(after));

            float point = colour * (mask * (0.25f + 0.75f * MathF.Max(Vector3.Dot(n, sun), 0f)));
            Assert.Equal(BitConverter.SingleToInt32Bits(point), BitConverter.SingleToInt32Bits(point * visibility));
            compared++;
        }

        Assert.Equal(2000, compared);
    }
}
