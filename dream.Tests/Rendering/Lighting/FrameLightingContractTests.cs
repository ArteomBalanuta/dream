using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// Wave T1c: the FrameLighting uniform block, its CPU mirror, the shader include mechanism, and the
// promise that ReferenceNoon looks like the old hard-coded point lights at spawn. Since T2d the
// day/night noon (SkyModelKeyframes.Noon) has a higher sun and is no longer ReferenceNoon; these noon
// tests feed ReferenceNoon directly, so they still prove the shaders reproduce the legacy look.
public class FrameLightingContractTests
{
    private static readonly string[] WorldFragmentShaders = ["cubeFragment.glsl", "enemyFragment.glsl"];

    // The spawn point the old point lights were matched at.
    private static readonly Vector3 Spawn = new(5f, 0.5f, 5f);

    private static readonly Vector3[] FaceNormals =
    [
        Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
    ];

    [Fact]
    public void The_Struct_Is_144_Bytes_Of_Nine_Std140_Vec4s()
    {
        Assert.Equal(FrameLighting.SizeInBytes, Marshal.SizeOf<FrameLighting>());
        Assert.Equal(144, FrameLighting.SizeInBytes);

        FieldInfo[] fields = OrderedFields();
        Assert.Equal(9, fields.Length);
        for (int i = 0; i < fields.Length; i++)
        {
            Assert.Equal(typeof(Vector4), fields[i].FieldType);
            Assert.Equal(i * 16, (int)Marshal.OffsetOf<FrameLighting>(fields[i].Name));
        }
    }

    [Fact]
    public void The_Glsl_Block_Matches_The_Struct_Member_For_Member()
    {
        string lighting = File.ReadAllText(Path.Combine(ShaderDirectory(), "lighting.glsl"));
        Match block = Regex.Match(lighting, @"layout\(std140\)\s+uniform\s+(\w+)\s*\{(.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, "No std140 uniform block in lighting.glsl.");
        Assert.Equal(FrameLighting.BlockName, block.Groups[1].Value);

        MatchCollection members = Regex.Matches(block.Groups[2].Value, @"(\w+)\s+(\w+)\s*;");
        FieldInfo[] fields = OrderedFields();
        Assert.Equal(fields.Length, members.Count);
        for (int i = 0; i < fields.Length; i++)
        {
            Assert.Equal("vec4", members[i].Groups[1].Value);
            string glslName = members[i].Groups[2].Value;
            Assert.StartsWith("u", glslName);
            // uLightingParams mirrors Params; every other member is "u" + the field name.
            Assert.EndsWith(fields[i].Name, glslName);
        }
    }

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void World_Fragment_Shaders_Include_The_Shared_Lighting_And_Drop_The_Point_Light(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(ShaderDirectory(), fileName));

        Assert.Single(Regex.Matches(source, @"^\s*#include ""lighting\.glsl""\s*$", RegexOptions.Multiline));
        Assert.DoesNotContain("lightPos", source);
        Assert.DoesNotContain("ambientStrength", source);
        Assert.Contains("dreamApplyFog(", source);
        Assert.Contains("dreamExpose(", source);
    }

    [Fact]
    public void Enemy_Shader_Takes_Its_Ambient_Scale_From_The_Frame_And_Floors_Before_Exposure()
    {
        string source = File.ReadAllText(Path.Combine(ShaderDirectory(), "enemyFragment.glsl"));

        // The actor ambient scale is FrameLighting.Params.z, set per time of day, not a constant.
        Assert.DoesNotContain("const float ActorAmbientScale", source);
        Assert.Contains("const float ActorDirectScale = 0.75;", source);
        // Wave T2 C0: the material emissive is added after exposure and before fog. Wave T2a: point
        // lights count as direct light (scaled by ActorDirectScale) and are added before the floor.
        Assert.Matches(
            @"lit = \(dreamAmbient\(n\) \* uLightingParams\.z \+ \(dreamDirect\(n\) \+ dreamPointLights\(n, fragPos\)\) \* ActorDirectScale\) \* baseColor\.rgb;\s*lit = max\(lit, baseColor\.rgb \* uLightingParams\.y\);\s*vec3 emissive = material\.emissiveFactor;\s*if \(material\.hasEmissiveTexture\) \{[^}]*\}\s*FragColor = vec4\(dreamApplyFog\(dreamExpose\(lit\) \+ emissive, fragPos\), baseColor\.a\);",
            source);
    }

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void Resolved_World_Shaders_Keep_Version_First_And_Take_The_Mask_Define(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(ShaderDirectory(), fileName));

        string resolved = Shader.ResolveIncludes(source, ShaderDirectory());
        string[] lines = Shader.InsertDefine(resolved, Shader.AlphaMaskDefine).Split('\n');

        Assert.StartsWith("#version 330 core", lines[0].TrimStart('﻿'));
        Assert.Equal($"#define {Shader.AlphaMaskDefine}", lines[1]);
        Assert.DoesNotMatch(new Regex(@"^\s*#\s*include\b", RegexOptions.Multiline), resolved);
        Assert.Contains("uniform FrameLighting", resolved);
        Assert.Single(Regex.Matches(resolved, @"^﻿?\s*#\s*version\b", RegexOptions.Multiline));
    }

    [Fact]
    public void An_Include_Line_Is_Replaced_By_The_File_Text()
    {
        using TempShaderDirectory directory = new();
        directory.Write("part.glsl", "﻿vec3 partColor() { return vec3(1.0); }\r\n");
        string source = "#version 330 core\r\n#include \"part.glsl\"\r\nvoid main() {}\r\n";

        string resolved = Shader.ResolveIncludes(source, directory.Path);

        Assert.Equal("#version 330 core\r\nvec3 partColor() { return vec3(1.0); }\r\nvoid main() {}\r\n", resolved);
        // A source without includes comes back unchanged.
        const string plain = "#version 330 core\r\n// mentions #include in a comment\r\nvoid main() {}";
        Assert.Equal(plain, Shader.ResolveIncludes(plain, directory.Path));
    }

    [Fact]
    public void A_Missing_Include_Throws()
    {
        using TempShaderDirectory directory = new();

        Assert.Throws<FileNotFoundException>(() =>
            Shader.ResolveIncludes("#version 330 core\n#include \"absent.glsl\"\n", directory.Path));
    }

    [Theory]
    [InlineData("#include \"other.glsl\"\nfloat a;\n")]
    [InlineData("float a;\n  #include \"other.glsl\"\n")]
    [InlineData("#version 330 core\nfloat a;\n")]
    public void A_Nested_Include_Or_A_Version_In_An_Include_Throws(string includedText)
    {
        using TempShaderDirectory directory = new();
        directory.Write("other.glsl", "float b;\n");
        directory.Write("part.glsl", includedText);

        Assert.Throws<InvalidOperationException>(() =>
            Shader.ResolveIncludes("#version 330 core\n#include \"part.glsl\"\n", directory.Path));
    }

    [Fact]
    public void An_Include_Outside_The_Shader_Directory_Throws()
    {
        using TempShaderDirectory directory = new();

        Assert.Throws<InvalidOperationException>(() =>
            Shader.ResolveIncludes("#version 330 core\n#include \"../part.glsl\"\n", directory.Path));
    }

    [Fact]
    public void Shader_Resolves_Includes_For_Both_Stages_Before_The_Define_And_Binds_The_Block_After_Link()
    {
        string shader = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Shader.cs"));

        Assert.Contains("string vertexSource = ResolveIncludes(File.ReadAllText(vertexPath), DirectoryOf(vertexPath));", shader);
        Assert.Contains("string fragmentSource = ResolveIncludes(File.ReadAllText(fragmentPath), DirectoryOf(fragmentPath));", shader);
        Assert.True(
            shader.IndexOf("ResolveIncludes(File.ReadAllText(fragmentPath)", StringComparison.Ordinal) <
            shader.IndexOf("fragmentSource = InsertDefine(", StringComparison.Ordinal));
        // After link and after the pinned location lines.
        Assert.Matches(
            @"Handle = program;\s*_modelLocation = GetUniformLocation\(ModelUniformName\);\s*_normalMatrixLocation = GetUniformLocation\(NormalMatrixUniformName\);[^{}]*?FrameLightingBuffer\.BindProgramBlock\(program\);",
            shader);

        string buffer = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Lighting", "FrameLightingBuffer.cs"));
        Assert.Matches(
            @"int block = GL\.GetUniformBlockIndex\(program, FrameLighting\.BlockName\);\s*if \(block < 0\)\s*\{\s*return false;\s*\}\s*GL\.UniformBlockBinding\(program, block, FrameLighting\.BindingPoint\);",
            buffer);
    }

    [Fact]
    public void The_World_Pass_Uploads_Once_And_The_View_Model_Uses_Its_Own_Range()
    {
        string world = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "World", "WorldRenderer.cs"));
        Assert.Matches(
            @"public WorldRenderStats DrawWorld\(WorldSessionPresentationState state\)\s*\{[^}]*_render\.UploadFrameLighting\(state\.Camera\);\s*return DrawWorld\(",
            world);
        Assert.Single(Regex.Matches(world, @"UploadFrameLighting\("));

        // Wave H3 adds the optional off-hand draw after the hands and the held item, in the same bracket.
        string viewModels = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "ViewModels", "ViewModelRenderer.cs"));
        Assert.Matches(
            @"_render\.BindViewModelLighting\(\);\s*try\s*\{\s*DrawSlot\([^;]*;\s*DrawSlot\([^;]*;\s*(?:if \([^)]*\)\s*\{\s*DrawSlot\([^;]*;\s*\}\s*)?\}\s*finally\s*\{\s*_render\.BindWorldLighting\(\);",
            viewModels);

        string buffer = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Lighting", "FrameLightingBuffer.cs"));
        Assert.Contains("GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, FrameLighting.SizeInBytes, ref world);", buffer);
        Assert.Contains("GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)ViewModelOffset, FrameLighting.SizeInBytes, ref viewModel);", buffer);

        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        Assert.Matches(@"RunDisposeStep\(DisposeFrameLighting,\s*ref failures\)", render);
        Assert.Matches(@"InitializeShaders\(\);\s*InitializeFrameLighting\(\);", render);
    }

    [Theory]
    [InlineData(144, 256, 256)]
    [InlineData(144, 16, 144)]
    [InlineData(144, 64, 192)]
    [InlineData(144, 1, 144)]
    [InlineData(144, 0, 144)]
    public void The_View_Model_Range_Starts_At_The_Aligned_Stride(int size, int alignment, int expected)
    {
        Assert.Equal(expected, FrameLightingBuffer.RoundUp(size, alignment));
    }

    [Fact]
    public void Reference_Noon_Has_No_Fog_And_Unit_Exposure()
    {
        FrameLighting noon = FrameLighting.ReferenceNoon;

        Assert.Equal(0f, noon.FogColor.W);
        Assert.Equal(1f, noon.Params.X);
        // Today's actor ambient: 0.2 x 3.25 = 0.65.
        Assert.Equal(3.25f, noon.Params.Z);
        Assert.Equal(0f, noon.Params.W);
        Assert.Equal(1f, noon.SunDirection.Xyz.Length, 5);
        // The sun is where the old cube point light was, seen from spawn.
        Vector3 legacy = Vector3.Normalize(new Vector3(4f, 2f, 2f) - Spawn);
        Assert.Equal(legacy.X, noon.SunDirection.X, 5);
        Assert.Equal(legacy.Y, noon.SunDirection.Y, 5);
        Assert.Equal(legacy.Z, noon.SunDirection.Z, 5);
    }

    public static TheoryData<Vector3> BaseColors() => new()
    {
        new Vector3(1f, 1f, 1f),
        new Vector3(0.5f, 0.5f, 0.5f),
        new Vector3(0.36f, 0.62f, 0.22f),
        new Vector3(0.55f, 0.41f, 0.28f),
    };

    [Theory]
    [MemberData(nameof(BaseColors))]
    public void Noon_Cube_Lighting_Matches_The_Old_Point_Light_At_Spawn(Vector3 baseColor)
    {
        FrameLighting world = FrameLightingBuffer.ForWorld(FrameLighting.ReferenceNoon, Spawn + new Vector3(0f, 1.6f, 3f));
        foreach (Vector3 normal in FaceNormals)
        {
            Vector3 legacy = LegacyCube(normal, Spawn, baseColor);
            Vector3 current = Cube(in world, normal, Spawn, baseColor);
            AssertClose(legacy, current, 1e-4f);
        }
    }

    [Theory]
    [MemberData(nameof(BaseColors))]
    public void Noon_Enemy_Lighting_Is_Close_To_The_Old_Point_Light_At_Spawn(Vector3 baseColor)
    {
        FrameLighting world = FrameLightingBuffer.ForWorld(FrameLighting.ReferenceNoon, Spawn + new Vector3(0f, 1.6f, 3f));
        foreach (Vector3 normal in FaceNormals)
        {
            // Compared as displayed: the framebuffer saturates at 1.
            Vector3 legacy = Saturate(LegacyEnemy(normal, Spawn, baseColor));
            Vector3 current = Saturate(Enemy(in world, normal, Spawn, baseColor));
            AssertClose(legacy, current, 0.15f);
        }
    }

    [Fact]
    public void Noon_Terrain_Luminance_At_Spawn_Is_Within_Five_Percent()
    {
        FrameLighting world = FrameLightingBuffer.ForWorld(FrameLighting.ReferenceNoon, Spawn);
        Vector3 grass = new(0.36f, 0.62f, 0.22f);
        float legacy = 0f;
        float current = 0f;
        foreach (Vector3 normal in FaceNormals)
        {
            legacy += Luminance(Saturate(LegacyCube(normal, Spawn, grass)));
            current += Luminance(Saturate(Cube(in world, normal, Spawn, grass)));
        }

        Assert.InRange(current, legacy * 0.95f, legacy * 1.05f);
    }

    [Fact]
    public void The_World_Range_Carries_The_Camera_And_Keeps_Everything_Else()
    {
        FrameLighting lighting = FrameLighting.ReferenceNoon;
        lighting.CameraPosition = new Vector4(9f, 9f, 9f, 12f);

        FrameLighting world = FrameLightingBuffer.ForWorld(in lighting, new Vector3(1f, 2f, 3f));

        Assert.Equal(new Vector4(1f, 2f, 3f, 12f), world.CameraPosition);
        Assert.Equal(lighting.SunDirection, world.SunDirection);
        Assert.Equal(lighting.AmbientSky, world.AmbientSky);
        Assert.Equal(lighting.FogColor, world.FogColor);
        Assert.Equal(lighting.Params, world.Params);
    }

    [Fact]
    public void The_View_Model_Range_Is_In_Eye_Space_With_An_Ambient_Floor()
    {
        FrameLighting night = FrameLighting.ReferenceNoon;
        night.SunDirection = new Vector4(Vector3.Normalize(new Vector3(1f, 1f, 0f)), 0f);
        night.MoonDirection = new Vector4(Vector3.Normalize(new Vector3(-1f, -1f, 0f)), 0.4f);
        night.AmbientSky = new Vector4(0.02f, 0.03f, 0.2f, 0f);
        night.AmbientGround = new Vector4(0.01f, 0.01f, 0.01f, 0f);
        night.CameraPosition = new Vector4(5f, 5f, 5f, 16f);

        // Camera at (3,1,-2) looking along +X: eye-space forward (-Z) is world +X.
        Vector3 eye = new(3f, 1f, -2f);
        Matrix4 view = Matrix4.LookAt(eye, eye + Vector3.UnitX, Vector3.UnitY);

        FrameLighting viewModel = FrameLightingBuffer.ForViewModel(in night, in view);

        // A world direction lands where the vertex shader's `view * v` would put it.
        Vector3 expectedSun = (new Vector4(night.SunDirection.Xyz, 0f) * view).Xyz;
        AssertClose(expectedSun, viewModel.SunDirection.Xyz, 1e-5f);
        // Eye space: +X right (world +Z here), +Y up, -Z forward (world +X here).
        AssertClose(new Vector3(0f, MathF.Sqrt(0.5f), -MathF.Sqrt(0.5f)), viewModel.SunDirection.Xyz, 1e-5f);
        Assert.Equal(0f, viewModel.SunDirection.W);
        Assert.Equal(0.4f, viewModel.MoonDirection.W);
        Assert.Equal(1f, viewModel.MoonDirection.Xyz.Length, 5);
        Assert.Equal(new Vector4(0f, 0f, 0f, 16f), viewModel.CameraPosition);
        float floor = FrameLightingBuffer.ViewModelAmbientFloor;
        Assert.Equal(new Vector4(floor, floor, 0.2f, 0f), viewModel.AmbientSky);
        Assert.Equal(new Vector4(floor, floor, floor, 0f), viewModel.AmbientGround);
        Assert.Equal(night.FogColor, viewModel.FogColor);
        Assert.Equal(night.Params, viewModel.Params);
    }

    [Fact]
    public void Building_Both_Ranges_Allocates_Nothing()
    {
        FrameLighting lighting = FrameLighting.ReferenceNoon;
        Matrix4 view = Matrix4.LookAt(Spawn, Spawn + Vector3.UnitZ, Vector3.UnitY);
        float sink = 0f;

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            FrameLighting world = FrameLightingBuffer.ForWorld(in lighting, Spawn);
            FrameLighting viewModel = FrameLightingBuffer.ForViewModel(in lighting, in view);
            sink += world.CameraPosition.X + viewModel.SunDirection.X;
        });

        Assert.Equal(0, bytes);
        GC.KeepAlive(sink);
    }

    // The pre-T1 cubeFragment.glsl: point light (4,2,2), ambient 0.2, white light.
    private static Vector3 LegacyCube(Vector3 normal, Vector3 fragPos, Vector3 baseColor)
    {
        Vector3 lightDir = Vector3.Normalize(new Vector3(4f, 2f, 2f) - fragPos);
        float diffuse = MathF.Max(Vector3.Dot(Vector3.Normalize(normal), lightDir), 0f);
        return (0.2f + diffuse) * baseColor;
    }

    // The pre-T1 enemyFragment.glsl: point light (4,4,2), ambient 0.65, diffuse x0.75, floor 0.35.
    private static Vector3 LegacyEnemy(Vector3 normal, Vector3 fragPos, Vector3 baseColor)
    {
        Vector3 lightDir = Vector3.Normalize(new Vector3(4f, 4f, 2f) - fragPos);
        float diffuse = MathF.Max(Vector3.Dot(Vector3.Normalize(normal), lightDir), 0f);
        Vector3 lit = (0.65f + diffuse * 0.75f) * baseColor;
        return Vector3.ComponentMax(lit, baseColor * 0.35f);
    }

    // CPU mirror of the new cubeFragment.glsl over lighting.glsl.
    private static Vector3 Cube(in FrameLighting l, Vector3 normal, Vector3 fragPos, Vector3 baseColor)
    {
        Vector3 n = Vector3.Normalize(normal);
        Vector3 lit = Expose(in l, (Ambient(in l, n) + Direct(in l, n)) * baseColor);
        return ApplyFog(in l, lit, fragPos);
    }

    // CPU mirror of the new enemyFragment.glsl over lighting.glsl.
    private static Vector3 Enemy(in FrameLighting l, Vector3 normal, Vector3 fragPos, Vector3 baseColor)
    {
        Vector3 n = Vector3.Normalize(normal);
        Vector3 lit = (Ambient(in l, n) * l.Params.Z + Direct(in l, n) * 0.75f) * baseColor;
        lit = Vector3.ComponentMax(lit, baseColor * l.Params.Y);
        return ApplyFog(in l, Expose(in l, lit), fragPos);
    }

    private static Vector3 Ambient(in FrameLighting l, Vector3 n) =>
        Vector3.Lerp(l.AmbientGround.Xyz, l.AmbientSky.Xyz, n.Y * 0.5f + 0.5f);

    private static Vector3 Direct(in FrameLighting l, Vector3 n) =>
        MathF.Max(Vector3.Dot(n, l.SunDirection.Xyz), 0f) * l.SunDirection.W * l.SunColor.Xyz
        + MathF.Max(Vector3.Dot(n, l.MoonDirection.Xyz), 0f) * l.MoonDirection.W * l.MoonColor.Xyz;

    private static Vector3 Expose(in FrameLighting l, Vector3 c) => c * l.Params.X;

    private static Vector3 ApplyFog(in FrameLighting l, Vector3 c, Vector3 fragPos)
    {
        float d = MathF.Max(Vector3.Distance(fragPos, l.CameraPosition.Xyz) - l.CameraPosition.W, 0f);
        float k = l.FogColor.W * d;
        return Vector3.Lerp(l.FogColor.Xyz, c, Math.Clamp(MathF.Pow(2f, -k * k), 0f, 1f));
    }

    private static Vector3 Saturate(Vector3 c) => Vector3.Clamp(c, Vector3.Zero, Vector3.One);

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(
            MathF.Abs(expected.X - actual.X) <= tolerance &&
            MathF.Abs(expected.Y - actual.Y) <= tolerance &&
            MathF.Abs(expected.Z - actual.Z) <= tolerance,
            $"Expected {expected}, got {actual} (tolerance {tolerance}).");
    }

    private static FieldInfo[] OrderedFields() => typeof(FrameLighting)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .OrderBy(field => (int)Marshal.OffsetOf<FrameLighting>(field.Name))
        .ToArray();

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

    private sealed class TempShaderDirectory : IDisposable
    {
        public TempShaderDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dream-shader-include-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string fileName, string text) => File.WriteAllText(System.IO.Path.Combine(Path, fileName), text);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
