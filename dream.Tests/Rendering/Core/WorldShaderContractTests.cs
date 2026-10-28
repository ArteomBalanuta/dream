using System.IO;
using System.Text.RegularExpressions;
using Dream.Assets;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// GPU-7: the world-pass shaders compute no per-vertex inverse, the CPU uploads a normal matrix
// once per draw through cached locations, and only the MASK variant of a world program discards.
public class WorldShaderContractTests
{
    private static readonly string[] WorldFragmentShaders = ["cubeFragment.glsl", "enemyFragment.glsl"];

    [Fact]
    public void No_Vertex_Shader_Calls_Inverse()
    {
        string[] vertexShaders = Directory.GetFiles(ShaderDirectory(), "*.glsl")
            .Where(path => Path.GetFileName(path).Contains("vertex", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Contains(vertexShaders, path => Path.GetFileName(path) == "cubeVertex.glsl");
        Assert.All(vertexShaders, path =>
            Assert.DoesNotMatch(@"\binverse\s*\(", StripComments(File.ReadAllText(path))));
    }

    [Fact]
    public void Cube_Vertex_Shader_Uses_The_Cpu_Normal_Matrix_For_Non_Instanced_Draws()
    {
        string shader = StripComments(File.ReadAllText(Path.Combine(ShaderDirectory(), "cubeVertex.glsl")));

        Assert.Contains($"uniform mat3 {Shader.NormalMatrixUniformName};", shader);
        Assert.Matches(
            @"mat3 resolvedNormalMatrix = useInstancing \? cofactorNormalMatrix\(instanceModel\) : normalMatrix;",
            shader);
        Assert.Contains("normal = resolvedNormalMatrix * localNormal;", shader);
        // The instanced path derives the normal matrix from cross products, not an inverse.
        Assert.Matches(@"mat3\(cross\(c1, c2\), cross\(c2, c0\), cross\(c0, c1\)\)", shader);
    }

    [Fact]
    public void Every_Model_Upload_In_The_World_Paths_Goes_Through_SetModelMatrix()
    {
        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        string animatedModel = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Models", "AnimatedModel.cs"));

        foreach (string source in new[] { render, animatedModel })
        {
            Assert.DoesNotContain("SetMatrix4(\"model\"", source);
            Assert.DoesNotContain("\"normalMatrix\"", source);
        }

        Assert.Contains("shader.SetModelMatrix(model);", render);
        Assert.Contains("shader.SetModelMatrix(modelMatrix);", render);
        // A node's primitives share one model matrix, so it is uploaded once per node, before the
        // primitive loop, rather than per primitive.
        Assert.Matches(
            @"shader\.SetModelMatrix\(modelMatrix\);\s*Shader bound = shader;\s*foreach \(var primitive in primitives\)",
            animatedModel);
    }

    [Fact]
    public void Model_And_Normal_Matrix_Locations_Are_Resolved_Once_At_Link_Time()
    {
        string shader = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Shader.cs"));

        Assert.Single(Regex.Matches(shader, @"GetUniformLocation\(ModelUniformName\)"));
        Assert.Single(Regex.Matches(shader, @"GetUniformLocation\(NormalMatrixUniformName\)"));
        Assert.Single(Regex.Matches(shader, @"GetUniformLocation\(AlphaCutoffUniformName\)"));
        Assert.Matches(
            @"Handle = program;\s*_modelLocation = GetUniformLocation\(ModelUniformName\);\s*_normalMatrixLocation = GetUniformLocation\(NormalMatrixUniformName\);",
            shader);

        string setModelMatrix = MethodBody(shader, "public void SetModelMatrix(Matrix4 model)");
        Assert.DoesNotContain("GetUniformLocation", setModelMatrix);
        Assert.Contains("SetMatrix4(_modelLocation, model);", setModelMatrix);
        Assert.Contains("SetMatrix3(_normalMatrixLocation, NormalMatrix.FromModel(in model));", setModelMatrix);

        // Each upload is counted.
        string setMatrix3 = MethodBody(shader, "public void SetMatrix3(int location, Matrix3 matrix)");
        Assert.Contains("GL.UniformMatrix3(location, false, ref matrix);", setMatrix3);
        Assert.Contains("RenderCounters.CountUniformUpload();", setMatrix3);
        string setAlphaCutoff = MethodBody(shader, "public void SetAlphaCutoff(float alphaCutoff)");
        Assert.DoesNotContain("GetUniformLocation", setAlphaCutoff);
        Assert.Single(Regex.Matches(setAlphaCutoff, @"RenderCounters\.CountUniformUpload\(\);"));
    }

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void World_Fragment_Shaders_Discard_Below_The_Cutoff_Only_In_The_Mask_Variant(string fileName)
    {
        string shader = StripComments(File.ReadAllText(Path.Combine(ShaderDirectory(), fileName)));

        Assert.Matches(@"struct Material \{[^}]*\bfloat alphaCutoff;[^}]*\};", shader);
        Assert.Single(Regex.Matches(shader, @"\bdiscard\b"));
        // The one discard is compiled only into the MASK variant, so an opaque program has none.
        Assert.Matches(
            $@"#ifdef {Shader.AlphaMaskDefine}\s*if \(baseColor\.a < material\.alphaCutoff\) \{{\s*discard;\s*\}}\s*#endif",
            shader);
        string opaqueProgram = Regex.Replace(
            shader,
            $@"#ifdef {Shader.AlphaMaskDefine}.*?#endif",
            string.Empty,
            RegexOptions.Singleline);
        Assert.DoesNotMatch(@"\bdiscard\b", opaqueProgram);
        Assert.DoesNotContain("#else", shader);
    }

    [Fact]
    public void World_Programs_Are_Loaded_With_Their_Mask_Variant_And_Terrain_Uses_The_Opaque_One()
    {
        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));

        Assert.Matches(@"LoadWorldShader\(""cube"",\s*AssetPaths\.Shader\(""cubeVertex\.glsl""\),\s*AssetPaths\.Shader\(""cubeFragment\.glsl""\)\);", render);
        Assert.Matches(@"LoadWorldShader\(""enemy"",\s*AssetPaths\.Shader\(""cubeVertex\.glsl""\),\s*AssetPaths\.Shader\(""enemyFragment\.glsl""\)\);", render);
        Assert.Contains("shader.AttachAlphaMaskVariant(new Shader(vertexPath, fragmentPath, Shader.AlphaMaskDefine));", render);
        // Terrain binds the base "cube" program and never the variant.
        string bindTerrain = MethodBody(render, "private void BindTerrainMaterial(");
        Assert.Contains("Shader shader = GetShader(\"cube\");", bindTerrain);
        Assert.DoesNotContain("AlphaMaskVariant", bindTerrain);
    }

    [Fact]
    public void Animated_Models_Switch_To_The_Mask_Program_Per_Primitive_And_Switch_Back()
    {
        string animatedModel = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Models", "AnimatedModel.cs"));

        foreach (string method in new[] { "private void RenderNode(", "private void RenderNodeInstanced(" })
        {
            string body = MethodBody(animatedModel, method);
            Assert.Contains("Shader program = SelectProgram(shader, material);", body);
            Assert.Contains("ApplySkinning(program,", body);
            Assert.Contains("SetMaterialProperties(program, material);", body);
            Assert.Contains("RestoreProgram(shader, bound);", body);
        }

        string bindProgram = MethodBody(animatedModel, "private void BindProgram(");
        Assert.Contains("program.Use();", bindProgram);
        Assert.Contains("program.SetMatrix4(\"view\", _renderView);", bindProgram);
        Assert.Contains("program.SetMatrix4(\"projection\", _renderProjection);", bindProgram);
        Assert.Contains("program.SetModelMatrix(modelMatrix);", bindProgram);
    }

    [Theory]
    [InlineData(AlphaMode.OPAQUE, false)]
    [InlineData(AlphaMode.MASK, true)]
    [InlineData(AlphaMode.BLEND, false)]
    public void Only_Mask_Materials_Use_The_Alpha_Mask_Program(AlphaMode gltfMode, bool expected)
    {
        Assert.Equal(expected, AnimatedModel.UsesAlphaMaskProgram(gltfMode));
    }

    [Fact]
    public void A_Define_Goes_On_The_Line_After_The_Version_Directive()
    {
        string source = "﻿#version 330 core\r\n\r\nout vec4 FragColor;\r\n";

        string defined = Shader.InsertDefine(source, Shader.AlphaMaskDefine);

        Assert.Equal($"﻿#version 330 core\r\n#define {Shader.AlphaMaskDefine}\n\r\nout vec4 FragColor;\r\n", defined);
        Assert.Equal($"#version 330 core\n#define X\n", Shader.InsertDefine("#version 330 core", "X"));
        Assert.Equal($"#define X\nvoid main() {{}}", Shader.InsertDefine("void main() {}", "X"));
    }

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void The_Mask_Define_Lands_Right_After_The_Real_Shader_Version(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(ShaderDirectory(), fileName));

        string[] lines = Shader.InsertDefine(source, Shader.AlphaMaskDefine).Split('\n');

        Assert.StartsWith("#version 330 core", lines[0].TrimStart('﻿'));
        Assert.Equal($"#define {Shader.AlphaMaskDefine}", lines[1]);
    }

    [Theory]
    [InlineData(AlphaMode.OPAQUE, WorldAlphaMode.Opaque)]
    [InlineData(AlphaMode.MASK, WorldAlphaMode.Mask)]
    // Blending is off in the world pass and there is no sorted transparency: BLEND draws opaque.
    [InlineData(AlphaMode.BLEND, WorldAlphaMode.Opaque)]
    public void Gltf_Alpha_Modes_Map_Onto_The_World_Pass(AlphaMode gltfMode, int expected)
    {
        Assert.Equal(expected, AnimatedModel.ToWorldAlphaMode(gltfMode));
    }

    public static TheoryData<string> ShippedModelPaths() => new()
    {
        PlayerModelAssetDefinition.ModelPath,
        WolfAssetDefinition.ModelPath,
        DirtCubeAssetDefinition.ModelPath,
        GrassCubeAssetDefinition.ModelPath,
        StoneCubeAssetDefinition.ModelPath,
        AssetPaths.Animation("viewmodels/DebugHands.gltf"),
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf")
    };

    // Every shipped material is OPAQUE, so no shipped draw takes the discard path and the output
    // is unchanged by the MASK support.
    [Theory]
    [MemberData(nameof(ShippedModelPaths))]
    public void Shipped_Models_Are_Opaque_So_They_Never_Discard(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);

        Assert.All(model.LogicalMaterials, material =>
            Assert.Equal(WorldAlphaMode.Opaque, AnimatedModel.ToWorldAlphaMode(material.Alpha)));
    }

    public static TheoryData<Matrix4> ModelMatrices() => new()
    {
        Matrix4.Identity,
        Matrix4.CreateTranslation(3f, -7f, 11f),
        Matrix4.CreateScale(2f) * Matrix4.CreateTranslation(1f, 2f, 3f),
        // Non-uniform scale, the case where mat3(model) is not a valid normal matrix.
        Matrix4.CreateScale(0.25f, 3f, 1.5f),
        Matrix4.CreateScale(0.25f, 3f, 1.5f)
            * Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(0.3f, -1.1f, 2.2f))
            * Matrix4.CreateTranslation(-4f, 5f, 0.5f),
        Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(1.2f, 0.4f, -0.7f))
            * Matrix4.CreateScale(4f, 0.5f, 0.1f)
            * Matrix4.CreateTranslation(9f, 9f, 9f),
        // Mirrored (negative determinant).
        Matrix4.CreateScale(-1f, 2f, 0.5f) * Matrix4.CreateRotationY(0.8f),
        // The terrain fallback cube's visual transform.
        CubeTransforms.CreateCenteredVisualModelMatrix(new Vector3(10f, 2f, -3f), 0.5f, 1.25f)
    };

    [Theory]
    [MemberData(nameof(ModelMatrices))]
    public void Normal_Matrix_Matches_Invert_Then_Transpose(Matrix4 model)
    {
        Matrix3 expected = new(Matrix4.Transpose(Matrix4.Invert(model)));

        Matrix3 actual = NormalMatrix.FromModel(in model);

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                Assert.Equal(expected[row, column], actual[row, column], 4);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModelMatrices))]
    public void Normal_Matrix_Keeps_Normals_Perpendicular_To_Transformed_Surfaces(Matrix4 model)
    {
        // A surface spanned by two tangents, and its normal. OpenTK uses row vectors (v * M).
        Vector3 tangentA = Vector3.Normalize(new Vector3(1f, 2f, -0.5f));
        Vector3 tangentB = Vector3.Normalize(new Vector3(-0.3f, 0.4f, 1f));
        Vector3 surfaceNormal = Vector3.Cross(tangentA, tangentB);
        Matrix3 linear = new(model);

        Vector3 transformedA = tangentA * linear;
        Vector3 transformedB = tangentB * linear;
        Vector3 transformedNormal = Vector3.Normalize(surfaceNormal * NormalMatrix.FromModel(in model));

        Assert.Equal(0f, Vector3.Dot(transformedNormal, Vector3.Normalize(transformedA)), 4);
        Assert.Equal(0f, Vector3.Dot(transformedNormal, Vector3.Normalize(transformedB)), 4);
        // It points the same way as the geometric normal of the transformed surface.
        Assert.True(Vector3.Dot(transformedNormal, Vector3.Cross(transformedA, transformedB)) * MathF.Sign(linear.Determinant) > 0f);
    }

    [Fact]
    public void Normal_Matrix_Of_A_Zero_Scale_Model_Does_Not_Throw()
    {
        Matrix4 collapsed = Matrix4.CreateScale(0f, 1f, 1f) * Matrix4.CreateTranslation(1f, 2f, 3f);

        Matrix3 normalMatrix = NormalMatrix.FromModel(in collapsed);

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                Assert.True(float.IsFinite(normalMatrix[row, column]));
            }
        }
    }

    [Fact]
    public void Normal_Matrix_Computation_Allocates_Nothing()
    {
        Matrix4 model = Matrix4.CreateScale(0.25f, 3f, 1.5f)
            * Matrix4.CreateRotationX(0.7f)
            * Matrix4.CreateTranslation(1f, 2f, 3f);
        float sink = 0f;

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            Matrix3 normalMatrix = NormalMatrix.FromModel(in model);
            sink += normalMatrix.M11;
        });

        Assert.Equal(0, bytes);
        GC.KeepAlive(sink);
    }

    // The body of the method whose signature line is given, up to its matching closing brace.
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found.");
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
                return source.Substring(open, i - open + 1);
            }
        }

        throw new InvalidOperationException($"{signature} has no closing brace.");
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
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
