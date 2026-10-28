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
// once per draw through cached locations, and the fragment shaders discard only for glTF MASK.
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
        string render = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
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
            @"shader\.SetModelMatrix\(modelMatrix\);\s*foreach \(var primitive in primitives\)",
            animatedModel);
    }

    [Fact]
    public void Model_And_Normal_Matrix_Locations_Are_Resolved_Once_At_Link_Time()
    {
        string shader = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Shader.cs"));

        Assert.Single(Regex.Matches(shader, @"GetUniformLocation\(ModelUniformName\)"));
        Assert.Single(Regex.Matches(shader, @"GetUniformLocation\(NormalMatrixUniformName\)"));
        Assert.Single(Regex.Matches(shader, @"GetUniformLocation\(AlphaModeUniformName\)"));
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
        string setMaterialAlpha = MethodBody(shader, "public void SetMaterialAlpha(int alphaMode, float alphaCutoff)");
        Assert.DoesNotContain("GetUniformLocation", setMaterialAlpha);
        Assert.Equal(2, Regex.Matches(setMaterialAlpha, @"RenderCounters\.CountUniformUpload\(\);").Count);
    }

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void World_Fragment_Shaders_Discard_Only_Below_The_Cutoff_For_Mask(string fileName)
    {
        string shader = StripComments(File.ReadAllText(Path.Combine(ShaderDirectory(), fileName)));

        Assert.Matches(@"struct Material \{[^}]*\bint alphaMode;[^}]*\bfloat alphaCutoff;[^}]*\};", shader);
        Assert.Equal(WorldAlphaMode.Opaque, ReadIntConstant(shader, "ALPHA_MODE_OPAQUE"));
        Assert.Equal(WorldAlphaMode.Mask, ReadIntConstant(shader, "ALPHA_MODE_MASK"));
        Assert.Single(Regex.Matches(shader, @"\bdiscard\b"));
        Assert.Matches(
            @"if \(material\.alphaMode == ALPHA_MODE_MASK && baseColor\.a < material\.alphaCutoff\) \{\s*discard;\s*\}",
            shader);
    }

    [Fact]
    public void Terrain_Draws_Reset_The_Shared_Cube_Program_To_Opaque()
    {
        string render = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));

        Assert.Contains("shader.SetMaterialAlpha(WorldAlphaMode.Opaque, 0f);", render);
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

    private static int ReadIntConstant(string shader, string name)
    {
        Match match = Regex.Match(shader, $@"const int {name} = (\d+);");
        Assert.True(match.Success, $"{name} not declared.");
        return int.Parse(match.Groups[1].Value);
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
