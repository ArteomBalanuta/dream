using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace Dream.Tests.Rendering.Models;

public class AnimatedModelDataTests : IClassFixture<GltfModelRootFixture>
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<AnimatedModelData>>> SharedLoads = new();

    private readonly GltfModelRootFixture _models;

    public AnimatedModelDataTests(GltfModelRootFixture models)
    {
        _models = models;
    }

    public static TheoryData<string> ModelPaths() => new()
    {
        PlayerModelAssetDefinition.ModelPath,
        WolfAssetDefinition.ModelPath,
        DirtCubeAssetDefinition.ModelPath,
        GrassCubeAssetDefinition.ModelPath,
        StoneCubeAssetDefinition.ModelPath
    };

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public async Task Load_OffThread_ProducesConsistentGeometry(string modelPath)
    {
        AnimatedModelData data = await LoadSharedOffThread(modelPath);

        Assert.Equal(modelPath, data.SourcePath);
        Assert.Equal(data.Model.LogicalMeshes.Count, data.MeshPrimitives.Count);
        int primitiveCount = data.MeshPrimitives.Sum(primitives => primitives.Count);
        Assert.True(primitiveCount > 0);
        foreach (AnimatedModelPrimitiveData primitive in data.MeshPrimitives.SelectMany(primitives => primitives))
        {
            Assert.NotEmpty(primitive.Vertices);
            Assert.NotEmpty(primitive.Indices);
            Assert.All(primitive.Indices, index => Assert.True(index < primitive.Vertices.Length));
            Assert.InRange(primitive.MaterialIndex, -1, data.Materials.Count - 1);
        }
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public async Task Load_OffThread_DecodesEveryTextureAsRgba(string modelPath)
    {
        AnimatedModelData data = await LoadSharedOffThread(modelPath);

        Assert.Equal(data.Model.LogicalMaterials.Count, data.Materials.Count);
        foreach (DecodedTexture texture in data.Materials.SelectMany(GetTextures))
        {
            Assert.True(texture.Width > 0);
            Assert.True(texture.Height > 0);
            Assert.Equal(texture.Width * texture.Height * 4, texture.Rgba.Length);
        }
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public async Task Load_TexturePresence_MatchesGltfChannels(string modelPath)
    {
        AnimatedModelData data = await LoadSharedOffThread(modelPath);

        foreach (Material material in data.Model.LogicalMaterials)
        {
            AnimatedModelMaterialData decoded = data.Materials[material.LogicalIndex];
            Assert.Equal(HasTexture(material, "BaseColor"), decoded.BaseColorTexture != null);
            Assert.Equal(HasTexture(material, "Emissive"), decoded.EmissiveTexture != null);

            if (decoded.BaseColorTexture != null)
            {
                Assert.Equal(DecodeReference(material, "BaseColor"), decoded.BaseColorTexture.Rgba);
            }
        }
    }

    // The shaders sample only baseColor and emissive, so a normal, metallic-roughness or occlusion
    // map in the glTF is never decoded: the decoded material has no field for it, and the decoded
    // texture count is exactly the baseColor and emissive maps the glTF carries.
    [Theory]
    [MemberData(nameof(ModelPaths))]
    public async Task Load_DecodesOnlyBaseColorAndEmissiveMaps(string modelPath)
    {
        AnimatedModelData data = await LoadSharedOffThread(modelPath);

        string[] decodedFields = typeof(AnimatedModelMaterialData).GetFields()
            .Where(field => field.FieldType == typeof(DecodedTexture))
            .Select(field => field.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "BaseColorTexture", "EmissiveTexture" }, decodedFields);

        foreach (Material material in data.Model.LogicalMaterials)
        {
            AnimatedModelMaterialData decoded = data.Materials[material.LogicalIndex];
            int expected = (HasTexture(material, "BaseColor") ? 1 : 0) + (HasTexture(material, "Emissive") ? 1 : 0);
            Assert.Equal(expected, GetTextures(decoded).Count());
        }

        string source = ReadSource("dream/Rendering/Models/AnimatedModelData.cs");
        Assert.DoesNotContain("DecodeTexture(material, \"Normal\")", source);
        Assert.DoesNotContain("DecodeTexture(material, \"MetallicRoughness\")", source);
        Assert.DoesNotContain("DecodeTexture(material, \"Occlusion\")", source);
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public async Task Load_OffThread_SkinningMatchesAnalysis(string modelPath)
    {
        AnimatedModelData data = await LoadSharedOffThread(modelPath);
        GltfSkinningAnalysisResult analysis = GltfSkinningAnalysis.Analyze(modelPath);

        if (analysis.HasSkins && analysis.JointCount <= AnimatedModelData.MaxSupportedJoints)
        {
            Assert.NotNull(data.SkinJoints);
            Assert.Equal(analysis.JointCount, data.SkinJoints!.Count);
            Skin skin = data.Model.LogicalSkins[0];
            for (int i = 0; i < skin.JointsCount; i++)
            {
                (Node joint, System.Numerics.Matrix4x4 inverseBind) = skin.GetJoint(i);
                Assert.Equal(joint.LogicalIndex, data.SkinJoints[i].NodeIndex);
                Assert.Equal(inverseBind.ToOpenTK(), data.SkinJoints[i].InverseBindMatrix);
            }
        }
        else
        {
            Assert.Null(data.SkinJoints);
        }

        bool anySkinned = data.MeshPrimitives.SelectMany(primitives => primitives).Any(primitive => primitive.UsesSkinning);
        Assert.Equal(analysis.HasSkinnedPrimitives, anySkinned);
    }

    [Fact]
    public async Task Load_SkinnedAssets_HaveJoints()
    {
        Assert.NotNull((await LoadSharedOffThread(WolfAssetDefinition.ModelPath)).SkinJoints);
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public async Task Load_Vertices_MatchReferenceProcessing(string modelPath)
    {
        AnimatedModelData data = await LoadSharedOffThread(modelPath);
        // An independent parse, only read here (accessors and indices), so it is shared.
        ModelRoot reference = _models.Get(modelPath, "reference");

        foreach (Mesh mesh in reference.LogicalMeshes)
        {
            IReadOnlyList<AnimatedModelPrimitiveData> primitives = data.MeshPrimitives[mesh.LogicalIndex];
            Assert.Equal(mesh.Primitives.Count, primitives.Count);
            for (int p = 0; p < mesh.Primitives.Count; p++)
            {
                MeshPrimitive source = mesh.Primitives[p];
                AnimatedModelPrimitiveData loaded = primitives[p];
                AnimatedModelData.Vertex[] expected = ReferenceVertices(source);

                Assert.Equal(expected, loaded.Vertices);
                Assert.Equal(source.GetIndices().Select(index => (uint)index).ToArray(), loaded.Indices);
                Assert.Equal(source.DrawPrimitiveType, loaded.PrimitiveType);
                Assert.Equal(source.Material?.LogicalIndex ?? -1, loaded.MaterialIndex);
                Assert.Equal(
                    source.GetVertexAccessor("JOINTS_0") != null && source.GetVertexAccessor("WEIGHTS_0") != null,
                    loaded.UsesSkinning);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public async Task Load_IsDeterministic_AcrossThreadsAndOwnsItsModelAndPose(string modelPath)
    {
        AnimatedModelData first = await LoadSharedOffThread(modelPath);
        AnimatedModelData second = AnimatedModelData.Load(modelPath);

        Assert.Equal(Checksum(first), Checksum(second));

        Assert.NotSame(first.Model, second.Model);
        Assert.NotSame(first.Pose, second.Pose);
        Assert.Equal(first.Model.LogicalNodes.Count, first.Pose.NodeCount);
        Assert.Equal(first.Model.LogicalAnimations.Count, first.Animations.Count);
    }

    [Fact]
    public void Vertex_Layout_MatchesShaderAttributeOffsets()
    {
        Assert.Equal(19 * sizeof(float), AnimatedModelData.Vertex.Size);
        Assert.Equal(AnimatedModelData.Vertex.Size, System.Runtime.InteropServices.Marshal.SizeOf<AnimatedModelData.Vertex>());
        Assert.Equal(0, OffsetOf(nameof(AnimatedModelData.Vertex.Position)));
        Assert.Equal(3 * sizeof(float), OffsetOf(nameof(AnimatedModelData.Vertex.Normal)));
        Assert.Equal(6 * sizeof(float), OffsetOf(nameof(AnimatedModelData.Vertex.Tangent)));
        Assert.Equal(9 * sizeof(float), OffsetOf(nameof(AnimatedModelData.Vertex.TexCoord0)));
        Assert.Equal(11 * sizeof(float), OffsetOf(nameof(AnimatedModelData.Vertex.Joints)));
        Assert.Equal(15 * sizeof(float), OffsetOf(nameof(AnimatedModelData.Vertex.Weights)));
        Assert.Equal(64, AnimatedModelData.MaxSupportedJoints);
    }

    [Fact]
    public void Source_AnimatedModelData_HasNoGlCalls()
    {
        string source = ReadSource("dream/Rendering/Models/AnimatedModelData.cs");

        Assert.DoesNotContain("GL.", source);
        Assert.DoesNotContain("OpenTK.Graphics", source);
    }

    [Fact]
    public void EmissiveScroll_Extra_Reads_A_Two_Number_Array()
    {
        Assert.Equal(new Vector2(0.25f, -0.5f), Scroll("""{"dreamEmissiveScroll": [0.25, -0.5]}"""));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"other": 1}""")]
    [InlineData("""{"dreamEmissiveScroll": 1}""")]
    [InlineData("""{"dreamEmissiveScroll": [1]}""")]
    [InlineData("""{"dreamEmissiveScroll": [1, 2, 3]}""")]
    [InlineData("""{"dreamEmissiveScroll": ["a", "b"]}""")]
    [InlineData("""{"dreamEmissiveScroll": [1, null]}""")]
    [InlineData("""{"dreamEmissiveScroll": {"u": 1, "v": 2}}""")]
    [InlineData("""[1, 2]""")]
    public void EmissiveScroll_Extra_Absent_Or_Malformed_Is_Zero(string json)
    {
        Assert.Equal(Vector2.Zero, Scroll(json));
    }

    [Fact]
    public void EmissiveScroll_Without_Extras_Is_Zero()
    {
        Assert.Equal(Vector2.Zero, AnimatedModelData.ReadEmissiveUvScroll(null));
    }

    private static Vector2 Scroll(string json) =>
        AnimatedModelData.ReadEmissiveUvScroll(System.Text.Json.Nodes.JsonNode.Parse(json));

    [Fact]
    public void Source_TextureDecoder_SeparatesDecodeFromUpload()
    {
        string source = ReadSource("dream/Rendering/Core/TextureDecoder.cs");
        string decodeMethods = Slice(source, "public static DecodedTexture DecodeRgbaFlipped(string path)", "public static int UploadTexture2D");
        string uploadMethod = Slice(source, "public static int UploadTexture2D", null);

        Assert.DoesNotContain("GL.", decodeMethods);
        Assert.Contains("Image.Load", decodeMethods);
        Assert.DoesNotContain("Image.", uploadMethod);
        Assert.Contains("GLUtils.AssertRenderThread", uploadMethod);
    }

    // TextureDecoder is the one file that holds both halves, and the test above checks it
    // keeps them in separate methods.
    [Fact]
    public void Source_NoFileDecodesAndCreatesGlObjects()
    {
        string productionRoot = Path.Combine(FindRepoRoot(), "dream");
        string decoderPath = Path.Combine(productionRoot, "Rendering", "Core", "TextureDecoder.cs");
        List<string> offenders = [];
        foreach (string path in Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(productionRoot, path);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(decoderPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string source = File.ReadAllText(path);
            bool createsGlObjects = source.Contains("GL.Gen", StringComparison.Ordinal);
            if (createsGlObjects &&
                (source.Contains("Image.Load", StringComparison.Ordinal) ||
                 source.Contains("ModelRoot.Load", StringComparison.Ordinal)))
            {
                offenders.Add(relative);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Source_AnimatedModel_UploadConstructorAssertsRenderThread()
    {
        string source = ReadSource("dream/Rendering/Models/AnimatedModel.cs");
        string constructor = Slice(source, "public AnimatedModel(AnimatedModelData data)", "public string SourcePath");

        Assert.Contains(": this(AnimatedModelData.Load(filePath))", source);
        Assert.Contains("GLUtils.AssertRenderThread", constructor);
        Assert.Contains("data.MarkConsumed()", constructor);
    }

    // Each asset is loaded once on the thread pool and shared read-only across tests, which keeps
    // the suite's CPU load low. No test here builds an AnimatedModel or applies a pose.
    private static Task<AnimatedModelData> LoadSharedOffThread(string modelPath) =>
        SharedLoads.GetOrAdd(
            modelPath,
            path => new Lazy<Task<AnimatedModelData>>(() => Task.Run(() => AnimatedModelData.Load(path)))).Value;

    private static IEnumerable<DecodedTexture> GetTextures(AnimatedModelMaterialData material) =>
        new[]
        {
            material.BaseColorTexture,
            material.EmissiveTexture
        }.OfType<DecodedTexture>();

    private static bool HasTexture(Material material, string channelName)
    {
        MaterialChannel? channel = material.FindChannel(channelName);
        return channel.HasValue && channel.Value.Texture?.PrimaryImage != null;
    }

    // The pre-split AnimatedModel.LoadTexture decode, inlined.
    private static byte[] DecodeReference(Material material, string channelName)
    {
        SharpGLTF.Schema2.Image image = material.FindChannel(channelName)!.Value.Texture.PrimaryImage;
        using MemoryStream stream = new(image.Content.Content.ToArray());
        using Image<Rgba32> decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
        decoded.Mutate(x => x.Flip(FlipMode.Vertical));
        byte[] pixels = new byte[4 * decoded.Width * decoded.Height];
        decoded.CopyPixelDataTo(pixels);
        return pixels;
    }

    // The pre-split AnimatedModel.ProcessVertices, inlined.
    private static AnimatedModelData.Vertex[] ReferenceVertices(MeshPrimitive primitive)
    {
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        var texCoords0 = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
        var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

        return Enumerable.Range(0, positions.Count).Select(i =>
        {
            Vector2 uv = texCoords0 != null ? texCoords0[i].ToOpenTK() : Vector2.Zero;
            uv.Y = 1 - uv.Y;

            return new AnimatedModelData.Vertex
            {
                Position = positions[i].ToOpenTK(),
                Normal = normals?[i].ToOpenTK() ?? Vector3.Zero,
                Tangent = tangents?[i].ToOpenTKTan() ?? Vector3.Zero,
                TexCoord0 = uv,
                Joints = joints?[i].ToOpenTK() ?? Vector4.Zero,
                Weights = weights?[i].ToOpenTK() ?? Vector4.Zero
            };
        }).ToArray();
    }

    private static string Checksum(AnimatedModelData data)
    {
        HashCode hash = new();
        int vertexCount = 0;
        int indexCount = 0;
        foreach (AnimatedModelPrimitiveData primitive in data.MeshPrimitives.SelectMany(primitives => primitives))
        {
            foreach (AnimatedModelData.Vertex vertex in primitive.Vertices)
            {
                hash.Add(vertex.Position);
                hash.Add(vertex.Normal);
                hash.Add(vertex.Tangent);
                hash.Add(vertex.TexCoord0);
                hash.Add(vertex.Joints);
                hash.Add(vertex.Weights);
            }

            foreach (uint index in primitive.Indices)
            {
                hash.Add(index);
            }

            vertexCount += primitive.Vertices.Length;
            indexCount += primitive.Indices.Length;
        }

        foreach (DecodedTexture texture in data.Materials.SelectMany(GetTextures))
        {
            hash.AddBytes(texture.Rgba);
        }

        return $"{vertexCount}:{indexCount}:{hash.ToHashCode()}";
    }

    private static int OffsetOf(string field) =>
        (int)System.Runtime.InteropServices.Marshal.OffsetOf<AnimatedModelData.Vertex>(field);

    private static string Slice(string source, string start, string? end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing '{start}'.");
        if (end == null)
        {
            return source[startIndex..];
        }

        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing '{end}' after '{start}'.");
        return source[startIndex..endIndex];
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
