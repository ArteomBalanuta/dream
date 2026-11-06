using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dream.Rendering.Lighting;
using Dream.Rendering.Mist;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The std140 FireShadowList block, its CPU mirror and the shader plumbing around it. This repo has been bitten by
// std140 drift before (FrameLightingContractTests, LightListContractSmokeTests), so the block is pinned member for
// member against fireShadows.glsl, and the three places the GPU learns of it (binding point, link-time block binding,
// the view-model range) are pinned too.
public class FireShadowListContractTests
{
    [Fact]
    public void The_Struct_Is_400_Bytes_Of_Twenty_Five_Std140_Vec4s()
    {
        Assert.Equal(400, FireShadowList.SizeInBytes);
        Assert.Equal(FireShadowList.SizeInBytes, Marshal.SizeOf<FireShadowList>());
        Assert.Equal(FireShadowList.SizeInBytes, Unsafe.SizeOf<FireShadowList>());
        Assert.Equal(4 * 16, Unsafe.SizeOf<FireShadowVector4Slots>());
        Assert.Equal(16 * 16, Unsafe.SizeOf<FireShadowVector4Tiles>());
    }

    [Fact]
    public void The_Fields_Sit_At_The_Std140_Offsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<FireShadowList>(nameof(FireShadowList.Count)));
        Assert.Equal(16, (int)Marshal.OffsetOf<FireShadowList>(nameof(FireShadowList.Light)));
        Assert.Equal(80, (int)Marshal.OffsetOf<FireShadowList>(nameof(FireShadowList.Range)));
        Assert.Equal(144, (int)Marshal.OffsetOf<FireShadowList>(nameof(FireShadowList.Tile)));
    }

    [Fact]
    public void The_Block_Binds_To_Point_3_Which_Nothing_Else_Uses()
    {
        Assert.Equal(3, FireShadowList.BindingPoint);
        int[] points = [FrameLighting.BindingPoint, LightList.BindingPoint, BoundaryMistFrame.BindingPoint, FireShadowList.BindingPoint];
        Assert.Equal(points.Length, points.Distinct().Count());
        Assert.Equal("FireShadowList", FireShadowList.BlockName);
    }

    [Fact]
    public void The_Glsl_Block_Matches_The_Struct_Member_For_Member()
    {
        string source = FireShadowSamplingMirrorTests.Read("dream", "shaders", "fireShadows.glsl");
        Match block = Regex.Match(source, @"layout\(std140\)\s+uniform\s+(\w+)\s*\{(.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, "No std140 uniform block in fireShadows.glsl.");
        Assert.Equal(FireShadowList.BlockName, block.Groups[1].Value);

        MatchCollection members = Regex.Matches(block.Groups[2].Value, @"(\w+)\s+(\w+)(?:\[(\d+)\])?\s*;");
        Assert.Equal(4, members.Count);

        // (glsl type, glsl name, array length or 0, struct field, bytes the std140 layout gives it)
        (string Type, string Name, int Length, string Field, int Bytes)[] expected =
        [
            ("ivec4", "uFireShadowCount", 0, nameof(FireShadowList.Count), 16),
            ("vec4", "uFireShadowLight", Unsafe.SizeOf<FireShadowVector4Slots>() / 16, nameof(FireShadowList.Light), Unsafe.SizeOf<FireShadowVector4Slots>()),
            ("vec4", "uFireShadowRange", Unsafe.SizeOf<FireShadowVector4Slots>() / 16, nameof(FireShadowList.Range), Unsafe.SizeOf<FireShadowVector4Slots>()),
            ("vec4", "uFireShadowTile", Unsafe.SizeOf<FireShadowVector4Tiles>() / 16, nameof(FireShadowList.Tile), Unsafe.SizeOf<FireShadowVector4Tiles>()),
        ];

        int offset = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Type, members[i].Groups[1].Value);
            Assert.Equal(expected[i].Name, members[i].Groups[2].Value);
            int length = members[i].Groups[3].Success ? int.Parse(members[i].Groups[3].Value) : 0;
            Assert.Equal(expected[i].Length, length);

            // std140: a vec4 array has a 16 byte stride, so each member is exactly its length x 16 (ivec4 and vec4 alike).
            Assert.Equal(expected[i].Bytes, Math.Max(length, 1) * 16);
            Assert.Equal(offset, (int)Marshal.OffsetOf<FireShadowList>(expected[i].Field));
            offset += expected[i].Bytes;
        }

        Assert.Equal(FireShadowList.SizeInBytes, offset);
        // The struct carries nothing the block does not declare.
        Assert.Equal(4, typeof(FireShadowList).GetFields(BindingFlags.Public | BindingFlags.Instance).Length);
    }

    [Fact]
    public void The_Slot_And_Tile_Counts_Are_The_Settings_Not_Magic_Numbers()
    {
        Assert.Equal(ShadowSettings.MaxCastingFireCount, Unsafe.SizeOf<FireShadowVector4Slots>() / 16);
        Assert.Equal(ShadowSettings.MaxCastingFireCount * ShadowSettings.FacesPerLight, Unsafe.SizeOf<FireShadowVector4Tiles>() / 16);
        Assert.Equal(ShadowSettings.GridSide * ShadowSettings.GridSide, ShadowSettings.MaxCastingFireCount * ShadowSettings.FacesPerLight);

        // The shader hard-codes the same numbers in its loop bound and its tile index.
        string source = FireShadowSamplingMirrorTests.Read("dream", "shaders", "fireShadows.glsl");
        Assert.Contains($"min(uFireShadowCount.x, {ShadowSettings.MaxCastingFireCount})", source);
        Assert.Contains($"uFireShadowTile[slot * {ShadowSettings.FacesPerLight} + face]", source);
        Assert.Equal(ShadowSettings.AtlasUniform, "uFireShadowAtlas");
        Assert.Contains($"uniform sampler2DShadow {ShadowSettings.AtlasUniform};", source);
    }

    // ---- FromFrame ----

    [Fact]
    public void From_A_Frame_The_Block_Carries_The_Flickered_Position_The_Light_Index_The_Planes_And_The_Strength()
    {
        ShadowFrame frame = ShadowFrame.None;
        Span<FireShadowSlot> fires = frame.Fires;
        fires[0] = new FireShadowSlot { LightIndex = 7, Position = new Vector3(1.5f, 2.5f, 3.5f), Near = 0.1f, Far = 11.5f, Strength = 1f };
        fires[1] = new FireShadowSlot { LightIndex = 2, Position = new Vector3(-4f, 1f, 9f), Near = 0.1f, Far = 9.2f, Strength = 0.75f };
        frame.FireCount = 2;

        FireShadowList list = FireShadowList.FromFrame(in frame);

        Assert.Equal(2, list.Count);
        Assert.Equal(new Vector4(1.5f, 2.5f, 3.5f, 7f), list.Light[0]);
        Assert.Equal(new Vector4(-4f, 1f, 9f, 2f), list.Light[1]);
        float inner = (ShadowSettings.TileSide - 2f * ShadowSettings.TileGutter) / ShadowSettings.TileSide;
        Assert.Equal(new Vector4(0.1f, 11.5f, inner, 1f), list.Range[0]);
        Assert.Equal(new Vector4(0.1f, 9.2f, inner, 0.75f), list.Range[1]);
        // Unused slots stay zero, so a stale light index can never match.
        Assert.Equal(Vector4.Zero, list.Light[2]);
        Assert.Equal(Vector4.Zero, list.Range[3]);
        Assert.Equal(Vector4.Zero, list.Tile[8]);
        Assert.Equal(0.984375f, inner);
    }

    [Fact]
    public void The_Tile_Rectangles_Are_ShadowMapTargets_Tile_Origins_As_Uv()
    {
        ShadowFrame frame = ShadowFrame.None;
        frame.FireCount = 4;
        FireShadowList list = FireShadowList.FromFrame(in frame);

        for (int slot = 0; slot < 4; slot++)
        {
            for (int face = 0; face < 4; face++)
            {
                (int x, int y) = ShadowMapTargets.TileOrigin(slot, face, ShadowSettings.TileSide);
                Vector4 tile = list.Tile[slot * 4 + face];
                Assert.Equal((float)x / ShadowSettings.AtlasResolution, tile.X);
                Assert.Equal((float)y / ShadowSettings.AtlasResolution, tile.Y);
                Assert.Equal(0.25f, tile.Z);
                Assert.Equal(0.25f, tile.W);
            }
        }

        // Slot 2, face 3 is tile index 11: column 3, row 2.
        Assert.Equal(new Vector4(0.75f, 0.5f, 0.25f, 0.25f), list.Tile[2 * 4 + 3]);
    }

    [Fact]
    public void A_Frame_With_More_Fires_Than_Slots_Is_Clamped_And_A_Negative_Count_Is_Zero()
    {
        ShadowFrame frame = ShadowFrame.None;
        frame.FireCount = 9;
        Assert.Equal(4, FireShadowList.FromFrame(in frame).Count);
        frame.FireCount = -3;
        Assert.Equal(0, FireShadowList.FromFrame(in frame).Count);
    }

    [Fact]
    public void The_Disabled_Frame_Makes_An_Empty_Block_So_The_Shader_Returns_A_Literal_One()
    {
        FireShadowList list = FireShadowList.FromFrame(ShadowFrame.None);
        Assert.Equal(0, list.Count);
        Assert.Equal(Vector4.Zero, list.Light[0]);
        Assert.Equal(Vector4.Zero, list.Range[0]);
    }

    [Fact]
    public void The_Faces_Are_Plus_X_Minus_X_Plus_Z_Minus_Z()
    {
        Assert.Equal(Vector3.UnitX, FireShadowList.FaceForward(0));
        Assert.Equal(-Vector3.UnitX, FireShadowList.FaceForward(1));
        Assert.Equal(Vector3.UnitZ, FireShadowList.FaceForward(2));
        Assert.Equal(-Vector3.UnitZ, FireShadowList.FaceForward(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => FireShadowList.FaceForward(4));
        Assert.Equal(ShadowSettings.FacesPerLight, 4);
    }

    // ---- shader plumbing ----

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void The_World_Fragment_Shaders_Include_The_Fire_Shadows_Between_Lighting_And_Point_Lights(string fileName)
    {
        string source = FireShadowSamplingMirrorTests.Read("dream", "shaders", fileName);

        Assert.Single(Regex.Matches(source, @"^\s*#include ""fireShadows\.glsl""\s*$", RegexOptions.Multiline));
        int lighting = source.IndexOf("#include \"lighting.glsl\"", StringComparison.Ordinal);
        int fire = source.IndexOf("#include \"fireShadows.glsl\"", StringComparison.Ordinal);
        int points = source.IndexOf("#include \"pointLights.glsl\"", StringComparison.Ordinal);
        Assert.True(lighting >= 0 && lighting < fire && fire < points, "fireShadows.glsl must sit between lighting.glsl and pointLights.glsl: includes are one level and a function must be defined above its caller.");
    }

    [Fact]
    public void Only_The_Two_World_Shaders_Declare_The_Fire_Block_And_The_Include_Has_No_Include_Or_Version()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(FireShadowSamplingMirrorTests.RepoRoot(), "dream", "shaders"), "*.glsl"))
        {
            string name = Path.GetFileName(file);
            string text = File.ReadAllText(file);
            bool includes = Regex.IsMatch(text, @"^\s*#include ""fireShadows\.glsl""", RegexOptions.Multiline);
            Assert.Equal(name is "cubeFragment.glsl" or "enemyFragment.glsl", includes);
        }

        string fireSource = FireShadowSamplingMirrorTests.Read("dream", "shaders", "fireShadows.glsl");
        Assert.DoesNotMatch(@"(?m)^\s*#(include|version)\b", fireSource);
    }

    [Fact]
    public void The_Point_Light_Loop_Multiplies_In_The_Visibility_Through_An_Unchanged_Signature()
    {
        string source = FireShadowSamplingMirrorTests.Read("dream", "shaders", "pointLights.glsl");
        string fire = FireShadowSamplingMirrorTests.Read("dream", "shaders", "fireShadows.glsl");

        Assert.Contains("vec3 dreamPointLights(vec3 n, vec3 fragPos) {", source);
        Assert.Contains("float dreamFireVisibility(int light, vec3 fragPos, vec3 n)", fire);
        Assert.Single(Regex.Matches(source, @"dreamFireVisibility\(i, fragPos, n\)"));
        Assert.Contains("sum += uPointLightColor[i].rgb * (f * (0.25 + 0.75 * lambert)) * visibility;", source);
    }

    [Fact]
    public void The_Block_Is_Pointed_At_Its_Binding_Point_At_Link_Time_Or_It_Would_Alias_FrameLighting()
    {
        string shader = FireShadowSamplingMirrorTests.Read("dream", "Rendering", "Core", "Shader.cs");
        Assert.Matches(@"BoundaryMistBuffer\.BindProgramBlock\(program\);\s*FireShadowListBuffer\.BindProgramBlock\(program\);", shader);

        string buffer = FireShadowSamplingMirrorTests.Read("dream", "Rendering", "Shadows", "FireShadowListBuffer.cs");
        Assert.Matches(
            @"int block = GL\.GetUniformBlockIndex\(program, FireShadowList\.BlockName\);\s*if \(block < 0\)\s*\{\s*return false;\s*\}\s*GL\.UniformBlockBinding\(program, block, FireShadowList\.BindingPoint\);",
            buffer);
        Assert.Contains("GL.BindBufferRange(BufferRangeTarget.UniformBuffer, FireShadowList.BindingPoint, _handle, IntPtr.Zero, FireShadowList.SizeInBytes);", buffer);
        Assert.Contains("GL.BindBufferRange(BufferRangeTarget.UniformBuffer, FireShadowList.BindingPoint, _handle, (IntPtr)ViewModelOffset, FireShadowList.SizeInBytes);", buffer);
    }

    [Fact]
    public void The_View_Model_Range_Is_Always_An_Empty_Block_Because_Its_Fragments_Are_In_Eye_Space()
    {
        string buffer = FireShadowSamplingMirrorTests.Read("dream", "Rendering", "Shadows", "FireShadowListBuffer.cs");

        Assert.Contains("FireShadowList viewModel = default;", buffer);
        Assert.Contains("GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)ViewModelOffset, FireShadowList.SizeInBytes, ref viewModel);", buffer);
        Assert.Contains("GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, FireShadowList.SizeInBytes, ref worldCopy);", buffer);
        // A default block is count 0, which the shader answers with a literal 1.0 before any fetch.
        Assert.Equal(0, default(FireShadowList).Count);
    }
}
