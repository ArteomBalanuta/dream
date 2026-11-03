using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// Wave T2a: point lights. The LightList block and its CPU mirror, nearest-16 selection in the
// frustum, the collector, the eye-space view-model range, the shader wiring, the CPU mirror of
// dreamPointLights, and the DREAM_DEBUG_POINT_LIGHTS ring.
public class WaveT2PointLightTests
{
    // Camera at the origin looking down -Z (OpenTK LookAt), 60 degree vertical field of view.
    private static readonly Vector3 Eye = Vector3.Zero;
    private static readonly Matrix4 View = Matrix4.LookAt(Eye, -Vector3.UnitZ, Vector3.UnitY);
    private static readonly Matrix4 Projection =
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60f), 16f / 9f, 0.1f, 500f);

    private static ViewFrustum Frustum() => ViewFrustum.FromViewAndProjection(View, Projection);

    // ---- Layout ----

    [Fact]
    public void The_Struct_Is_528_Bytes_With_Fields_At_0_16_And_272()
    {
        Assert.Equal(528, Unsafe.SizeOf<LightList>());
        Assert.Equal(0, (int)Marshal.OffsetOf<LightList>(nameof(LightList.Count)));
        Assert.Equal(16, (int)Marshal.OffsetOf<LightList>(nameof(LightList.PositionRadius)));
        Assert.Equal(272, (int)Marshal.OffsetOf<LightList>(nameof(LightList.Color)));
    }

    [Fact]
    public void The_Glsl_Block_Matches_The_Struct_Member_For_Member()
    {
        string source = File.ReadAllText(Path.Combine(ShaderDirectory(), "pointLights.glsl"));
        Match block = Regex.Match(source, @"layout\(std140\)\s+uniform\s+(\w+)\s*\{(.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, "No std140 uniform block in pointLights.glsl.");
        Assert.Equal(LightList.BlockName, block.Groups[1].Value);

        MatchCollection members = Regex.Matches(block.Groups[2].Value, @"(\w+)\s+(\w+)\s*(?:\[(\d+)\])?\s*;");
        Assert.Equal(3, members.Count);

        // The public fields in offset order: Count (padded to an ivec4), PositionRadius, Color.
        FieldInfo[] fields = typeof(LightList)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(field => (int)Marshal.OffsetOf<LightList>(field.Name))
            .ToArray();
        Assert.Equal([nameof(LightList.Count), nameof(LightList.PositionRadius), nameof(LightList.Color)], fields.Select(f => f.Name));

        Assert.Equal("ivec4", members[0].Groups[1].Value);
        Assert.Equal("uPointLightCount", members[0].Groups[2].Value);
        Assert.False(members[0].Groups[3].Success);
        for (int i = 1; i < 3; i++)
        {
            Assert.Equal("vec4", members[i].Groups[1].Value);
            Assert.Equal("uPoint" + "Light" + fields[i].Name, members[i].Groups[2].Value);
            Assert.Equal(LightList.MaxLights, int.Parse(members[i].Groups[3].Value));
        }
    }

    // ---- Selection ----

    [Fact]
    public void Selection_Keeps_The_Sixteen_Nearest_In_View_Nearest_First()
    {
        // 40 lights straight ahead at 2, 3, ..., 41 m, given far to near.
        PointLight[] lights = new PointLight[40];
        for (int i = 0; i < lights.Length; i++)
        {
            float distance = 41 - i;
            lights[i] = new PointLight(new Vector3(0f, 0f, -distance), 1f, new Vector3(distance, 0f, 0f));
        }

        LightList list = default;
        LightSelection.SelectNearest(lights, Eye, Frustum(), ref list);

        Assert.Equal(LightList.MaxLights, list.Count);
        for (int i = 0; i < LightList.MaxLights; i++)
        {
            float expected = 2 + i;
            Assert.Equal(new Vector4(0f, 0f, -expected, 1f), list.PositionRadius[i]);
            Assert.Equal(new Vector4(expected, 0f, 0f, 0f), list.Color[i]);
        }
    }

    [Fact]
    public void A_Light_Behind_The_Camera_Is_Culled_Unless_Its_Sphere_Reaches_The_Frustum()
    {
        PointLight[] lights =
        [
            // 5 m behind, radius 2: its sphere stays behind the near plane.
            new(new Vector3(0f, 0f, 5f), 2f, Vector3.One),
            // 1 m behind, radius 3: its sphere reaches in front of the camera.
            new(new Vector3(0f, 0f, 1f), 3f, new Vector3(2f)),
        ];

        LightList list = default;
        LightSelection.SelectNearest(lights, Eye, Frustum(), ref list);

        Assert.Equal(1, list.Count);
        Assert.Equal(new Vector4(0f, 0f, 1f, 3f), list.PositionRadius[0]);
    }

    [Fact]
    public void Mirrored_Lights_At_Equal_Distance_Keep_Input_Order_And_A_Nearer_Late_Light_Wins()
    {
        // 20 lights mirrored left/right of the view axis: pairs at exactly the same distance.
        PointLight[] lights = new PointLight[20];
        for (int i = 0; i < lights.Length; i++)
        {
            float side = i % 2 == 0 ? 1f : -1f;
            lights[i] = new PointLight(new Vector3(side, 0f, -4f - (i / 2)), 1f, new Vector3(i, 0f, 0f));
        }

        LightList list = default;
        LightSelection.SelectNearest(lights, Eye, Frustum(), ref list);

        Assert.Equal(LightList.MaxLights, list.Count);
        for (int i = 0; i < LightList.MaxLights; i++)
        {
            Assert.Equal(i, list.Color[i].X);
        }

        // A nearer light later in the input still wins a slot and goes first; the farthest drops.
        PointLight[] withNear = [.. lights, new PointLight(new Vector3(0f, 0f, -2f), 1f, new Vector3(99f, 0f, 0f))];
        LightSelection.SelectNearest(withNear, Eye, Frustum(), ref list);
        Assert.Equal(LightList.MaxLights, list.Count);
        Assert.Equal(99f, list.Color[0].X);
        for (int i = 1; i < LightList.MaxLights; i++)
        {
            Assert.Equal(i - 1, list.Color[i].X);
        }
    }

    [Fact]
    public void Exact_Ties_Keep_The_Earliest_Lights()
    {
        PointLight[] lights = new PointLight[30];
        for (int i = 0; i < lights.Length; i++)
        {
            lights[i] = new PointLight(new Vector3(0f, 0f, -5f), 1f, new Vector3(i, 0f, 0f));
        }

        LightList list = default;
        LightSelection.SelectNearest(lights, Eye, Frustum(), ref list);

        Assert.Equal(LightList.MaxLights, list.Count);
        for (int i = 0; i < LightList.MaxLights; i++)
        {
            Assert.Equal(i, list.Color[i].X);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(LightCollector.Capacity)]
    public void Selection_Never_Writes_A_Count_Above_Sixteen(int candidates)
    {
        PointLight[] lights = new PointLight[candidates];
        for (int i = 0; i < candidates; i++)
        {
            lights[i] = new PointLight(new Vector3((i % 7) - 3f, (i % 5) - 2f, -2f - (i % 97)), 4f, Vector3.One);
        }

        LightList list = default;
        list.Count = 99;
        LightSelection.SelectNearest(lights, Eye, Frustum(), ref list);

        Assert.Equal(Math.Min(candidates, LightList.MaxLights), list.Count);
        for (int i = 1; i < list.Count; i++)
        {
            Assert.True(
                Vector3.DistanceSquared(list.PositionRadius[i - 1].Xyz, Eye) <= Vector3.DistanceSquared(list.PositionRadius[i].Xyz, Eye),
                "Selected lights are nearest first.");
        }
    }

    [Fact]
    public void Two_Spans_Select_As_If_Concatenated()
    {
        PointLight[] first = [new(new Vector3(0f, 0f, -9f), 1f, Vector3.UnitX), new(new Vector3(0f, 0f, -4f), 1f, Vector3.UnitY)];
        PointLight[] second = [new(new Vector3(0f, 0f, -2f), 1f, Vector3.UnitZ)];

        LightList split = default;
        LightSelection.SelectNearest(first, second, Eye, Frustum(), ref split);
        LightList joined = default;
        LightSelection.SelectNearest([.. first, .. second], Eye, Frustum(), ref joined);

        Assert.Equal(3, split.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(joined.PositionRadius[i], split.PositionRadius[i]);
            Assert.Equal(joined.Color[i], split.Color[i]);
        }

        Assert.Equal(new Vector4(Vector3.UnitZ, 0f), split.Color[0]);
    }

    [Fact]
    public void An_Invalid_Frustum_Accepts_Every_Light()
    {
        PointLight[] lights = [new(new Vector3(0f, 0f, 50f), 1f, Vector3.One)];
        LightList list = default;
        LightSelection.SelectNearest(lights, Eye, default(ViewFrustum), ref list);
        Assert.Equal(1, list.Count);
    }

    // ---- Collector ----

    [Fact]
    public void The_Collector_Rejects_Bad_Lights_And_Counts_What_It_Drops()
    {
        LightCollector collector = new();
        collector.Add(new PointLight(Vector3.Zero, 0f, Vector3.One));
        collector.Add(new PointLight(Vector3.Zero, -1f, Vector3.One));
        collector.Add(new PointLight(Vector3.Zero, float.NaN, Vector3.One));
        collector.Add(new PointLight(Vector3.Zero, float.PositiveInfinity, Vector3.One));
        collector.Add(new PointLight(new Vector3(float.NaN, 0f, 0f), 1f, Vector3.One));
        collector.Add(new PointLight(new Vector3(0f, float.PositiveInfinity, 0f), 1f, Vector3.One));
        collector.Add(new PointLight(new Vector3(0f, 0f, float.NegativeInfinity), 1f, Vector3.One));
        Assert.Equal(0, collector.Count);
        Assert.Equal(0, collector.Dropped);

        for (int i = 0; i < LightCollector.Capacity + 5; i++)
        {
            collector.Add(new PointLight(new Vector3(i, 0f, 0f), 1f, Vector3.One));
        }

        Assert.Equal(LightCollector.Capacity, collector.Count);
        Assert.Equal(5, collector.Dropped);
        Assert.Equal(LightCollector.Capacity - 1, collector.Lights[^1].Position.X);

        collector.Clear();
        Assert.Equal(0, collector.Count);
        Assert.Equal(0, collector.Dropped);
        Assert.True(collector.Lights.IsEmpty);
    }

    // ---- View-model range ----

    [Fact]
    public void The_View_Model_Range_Has_Eye_Space_Positions()
    {
        Vector3 eye = new(3f, 1f, -2f);
        Matrix4 view = Matrix4.LookAt(eye, eye + Vector3.UnitX, Vector3.UnitY);
        LightList world = default;
        world.Count = 2;
        world.PositionRadius[0] = new Vector4(5f, 1f, -2f, 4f);
        world.Color[0] = new Vector4(1f, 0.5f, 0.25f, 0f);
        world.PositionRadius[1] = new Vector4(3f, 4f, 1f, 6f);
        world.Color[1] = new Vector4(0.1f, 0.2f, 0.3f, 0f);

        LightList viewModel = default;
        LightSelection.ToEyeSpace(in world, in view, ref viewModel);

        Assert.Equal(2, viewModel.Count);
        for (int i = 0; i < 2; i++)
        {
            // Where the vertex shader's `view * vec4(p, 1)` puts it.
            Vector3 expected = (new Vector4(world.PositionRadius[i].Xyz, 1f) * view).Xyz;
            AssertClose(expected, viewModel.PositionRadius[i].Xyz, 1e-5f);
            Assert.Equal(world.PositionRadius[i].W, viewModel.PositionRadius[i].W);
            Assert.Equal(world.Color[i], viewModel.Color[i]);
        }

        // 2 m straight ahead of the camera is (0, 0, -2) in eye space.
        AssertClose(new Vector3(0f, 0f, -2f), viewModel.PositionRadius[0].Xyz, 1e-5f);
        // Distances to the eye are kept.
        Assert.Equal(
            Vector3.Distance(world.PositionRadius[1].Xyz, eye),
            viewModel.PositionRadius[1].Xyz.Length,
            4);
    }

    [Fact]
    public void Collect_Select_And_Eye_Space_Allocate_Nothing_Over_A_Thousand_Frames()
    {
        LightCollector collector = new();
        PointLight[] ring = new PointLight[Render.MaxDebugPointLights];
        ViewFrustum frustum = Frustum();
        LightList world = default;
        LightList viewModel = default;
        float sink = 0f;

        long bytes = AllocationAssert.MeasureBytesPerIteration(
            () =>
            {
                collector.Clear();
                for (int i = 0; i < 200; i++)
                {
                    collector.Add(new PointLight(new Vector3(i % 13, 1f, -2f - (i % 31)), 6f, Vector3.One));
                }

                Render.FillDebugPointLightRing(ring, Eye);
                LightSelection.SelectNearest(collector.Lights, ring, Eye, in frustum, ref world);
                LightSelection.ToEyeSpace(in world, in View, ref viewModel);
                sink += viewModel.PositionRadius[0].X + world.Count;
            },
            iterations: 1000);

        Assert.Equal(0, bytes);
        GC.KeepAlive(sink);
    }

    // ---- Shader mirror ----

    [Fact]
    public void The_Falloff_Is_One_At_The_Light_And_Zero_At_The_Radius()
    {
        Assert.Equal(1f, Falloff(0f, 8f));
        Assert.Equal(0f, Falloff(8f, 8f));
        Assert.Equal(0f, Falloff(12f, 8f));
        float half = Falloff(4f, 8f);
        Assert.Equal(0.5625f / 2.5f, half, 6); // (1 - 0.25)^2 / (1 + 6 * 0.25)
    }

    [Fact]
    public void Point_Lights_Add_Light_Within_Their_Radius_Only()
    {
        LightList list = default;
        list.Count = 1;
        list.PositionRadius[0] = new Vector4(0f, 2f, 0f, 8f);
        list.Color[0] = new Vector4(1.6f, 0.99f, 0.51f, 0f);

        // Up-facing floor right under the light: full lambert, falloff (1 - 4/64)^2 / (1 + 6 * 4/64).
        Vector3 under = PointLights(in list, Vector3.UnitY, Vector3.Zero);
        float f = MathF.Pow(1f - 4f / 64f, 2f) / (1f + 6f * 4f / 64f);
        AssertClose(list.Color[0].Xyz * f, under, 1e-5f);

        // A face turned away still gets the 0.25 wrap term.
        Vector3 away = PointLights(in list, -Vector3.UnitY, Vector3.Zero);
        AssertClose(list.Color[0].Xyz * (f * 0.25f), away, 1e-5f);

        // Past the radius: nothing.
        Assert.Equal(Vector3.Zero, PointLights(in list, Vector3.UnitY, new Vector3(0f, 2f, 9f)));

        // No lights: exactly nothing, so the T1 look is unchanged.
        LightList none = default;
        Assert.Equal(Vector3.Zero, PointLights(in none, Vector3.UnitY, Vector3.Zero));
    }

    // ---- Shader and render wiring ----

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void World_Fragment_Shaders_Include_Point_Lights_Right_After_Lighting(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(ShaderDirectory(), fileName));

        Assert.Matches(new Regex(@"^#include ""lighting\.glsl""\r?\n#include ""pointLights\.glsl""\r?$", RegexOptions.Multiline), source);
        Assert.Single(Regex.Matches(source, @"dreamPointLights\(n, fragPos\)"));

        string resolved = Shader.ResolveIncludes(source, ShaderDirectory());
        Assert.Contains("uniform LightList", resolved);
        Assert.True(resolved.IndexOf("uniform FrameLighting", StringComparison.Ordinal) < resolved.IndexOf("uniform LightList", StringComparison.Ordinal));
    }

    [Fact]
    public void Cube_Shader_Adds_Point_Lights_To_Ambient_And_Direct()
    {
        string source = File.ReadAllText(Path.Combine(ShaderDirectory(), "cubeFragment.glsl"));
        Assert.Contains("vec3 lit = (dreamAmbient(n) + dreamDirect(n) + dreamPointLights(n, fragPos)) * baseColor.rgb;", source);
        Assert.Contains("FragColor = vec4(dreamApplyFog(dreamExpose(lit) + emissive, fragPos), baseColor.a);", source);
    }

    [Fact]
    public void The_Block_Is_Bound_At_Link_And_Uploaded_Once_Per_Frame_With_The_Frame_Lighting()
    {
        string shader = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Shader.cs"));
        Assert.Matches(@"FrameLightingBuffer\.BindProgramBlock\(program\);\s*LightListBuffer\.BindProgramBlock\(program\);", shader);

        string buffer = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Lighting", "LightListBuffer.cs"));
        Assert.Matches(
            @"int block = GL\.GetUniformBlockIndex\(program, LightList\.BlockName\);\s*if \(block < 0\)\s*\{\s*return false;\s*\}\s*GL\.UniformBlockBinding\(program, block, LightList\.BindingPoint\);",
            buffer);
        Assert.Contains("ViewModelOffset = FrameLightingBuffer.RoundUp(LightList.SizeInBytes, alignment);", buffer);
        Assert.Single(Regex.Matches(buffer, @"RenderCounters\.CountUniformUpload\(\);"));

        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        Assert.Matches(
            @"_frameLightingBuffer\?\.Upload\([^;]*;\s*SelectPointLights\(cameraPosition, in view\);\s*_lightListBuffer\?\.Upload\(in _selectedLights, in view\);",
            render);
        Assert.Matches(@"_frameLightingBuffer\?\.BindWorld\(\);\s*_lightListBuffer\?\.BindWorld\(\);", render);
        Assert.Matches(@"_frameLightingBuffer\?\.BindViewModel\(\);\s*_lightListBuffer\?\.BindViewModel\(\);", render);
        Assert.Contains("_lightListBuffer = new LightListBuffer();", render);
        Assert.Contains("_lightListBuffer?.Dispose();", render);
    }

    [Theory]
    [InlineData(64, 528, 576)]
    [InlineData(256, 528, 768)]
    [InlineData(16, 528, 528)]
    [InlineData(0, 528, 528)]
    public void The_View_Model_Range_Starts_At_The_Aligned_Stride(int alignment, int size, int expected)
    {
        Assert.Equal(expected, FrameLightingBuffer.RoundUp(size, alignment));
    }

    // ---- Debug ring ----

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("0", 0)]
    [InlineData("-3", 0)]
    [InlineData("16", 16)]
    [InlineData("64", 64)]
    [InlineData("65", 64)]
    [InlineData("100000", 64)]
    public void The_Debug_Light_Count_Defaults_To_Zero_And_Is_Clamped(string? value, int expected)
    {
        Assert.Equal(expected, Render.ParseDebugPointLightCount(value));
    }

    [Fact]
    public void The_Debug_Ring_Is_Deterministic_And_Around_The_Camera()
    {
        Vector3 camera = new(10f, 3f, -4f);
        PointLight[] a = new PointLight[16];
        PointLight[] b = new PointLight[16];
        Render.FillDebugPointLightRing(a, camera);
        Render.FillDebugPointLightRing(b, camera);

        Assert.Equal(a, b);
        foreach (PointLight light in a)
        {
            Vector3 offset = light.Position - camera;
            Assert.Equal(Render.DebugRingHeight, offset.Y, 5);
            Assert.Equal(Render.DebugRingDistance, offset.Xz.Length, 4);
            Assert.Equal(Render.DebugLightRadius, light.Radius);
        }

        // All 16 equally near: selection keeps them (the ones in view) in ring order.
        LightList list = default;
        LightSelection.SelectNearest(a, camera, ViewFrustum.FromViewAndProjection(
            Matrix4.LookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY), Projection), ref list);
        Assert.InRange(list.Count, 1, LightList.MaxLights);
    }

    // CPU mirror of the falloff in pointLights.glsl.
    private static float Falloff(float distance, float radius)
    {
        float s = distance * distance / (radius * radius);
        float f = Math.Clamp(1f - s, 0f, 1f);
        return f * f / (1f + 6f * s);
    }

    // CPU mirror of dreamPointLights in pointLights.glsl.
    private static Vector3 PointLights(in LightList list, Vector3 n, Vector3 fragPos)
    {
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < list.Count; i++)
        {
            Vector3 toLight = list.PositionRadius[i].Xyz - fragPos;
            float r = list.PositionRadius[i].W;
            float d2 = Vector3.Dot(toLight, toLight);
            float s = d2 / (r * r);
            float f = Math.Clamp(1f - s, 0f, 1f);
            f = f * f / (1f + 6f * s);
            float lambert = MathF.Max(Vector3.Dot(n, toLight / MathF.Sqrt(MathF.Max(d2, 1e-4f))), 0f);
            sum += list.Color[i].Xyz * (f * (0.25f + 0.75f * lambert));
        }

        return sum;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(
            MathF.Abs(expected.X - actual.X) <= tolerance &&
            MathF.Abs(expected.Y - actual.Y) <= tolerance &&
            MathF.Abs(expected.Z - actual.Z) <= tolerance,
            $"Expected {expected}, got {actual} (tolerance {tolerance}).");
    }

    private static string ShaderDirectory() => Path.Combine(RepoRoot(), "dream", "shaders");

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
