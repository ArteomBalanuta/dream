using System.Runtime.InteropServices;
using Dream.Rendering.Core;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Vector2 = OpenTK.Mathematics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;

namespace Dream.Rendering.Models;

// Everything AnimatedModel computes on the CPU: the parsed glTF, vertex and index arrays,
// decoded textures, material factors, skin joints, animations, the shared skeleton and the rest
// pose. It has no GL dependency, so Load may run on any thread; AnimatedModel(AnimatedModelData)
// then only uploads.
//
// One data object belongs to exactly one AnimatedModel, which uploads its buffers once; the
// AnimatedModel constructor rejects a data object that has already been used. Actors animate
// through AnimatedModelInstance objects over the shared Skeleton, never through the model itself.
public sealed class AnimatedModelData
{
    public const int MaxSupportedJoints = 64;

    private static readonly bool EnableDiagnostics =
        string.Equals(Environment.GetEnvironmentVariable("DREAM_MODEL_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    private int _consumed;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;
        public Vector2 TexCoord0;
        public Vector4 Joints;
        public Vector4 Weights;

        public const int Size = (3 + 3 + 3 + 2 + 4 + 4) * sizeof(float);
    }

    private AnimatedModelData(
        string sourcePath,
        ModelRoot model,
        AnimatedModelMaterialData[] materials,
        AnimatedModelPrimitiveData[][] meshPrimitives,
        AnimatedModelSkinJoint[]? skinJoints,
        Dictionary<string, Animation> animations,
        AnimatedModelSkeleton skeleton)
    {
        SourcePath = sourcePath;
        Model = model;
        Materials = materials;
        MeshPrimitives = meshPrimitives;
        SkinJoints = skinJoints;
        Animations = animations;
        Skeleton = skeleton;
        Pose = new AnimatedModelPose(skeleton);
    }

    public string SourcePath { get; }

    public ModelRoot Model { get; }

    // Indexed by Material.LogicalIndex.
    public IReadOnlyList<AnimatedModelMaterialData> Materials { get; }

    // Indexed by Mesh.LogicalIndex; one entry per glTF primitive, in glTF order.
    public IReadOnlyList<IReadOnlyList<AnimatedModelPrimitiveData>> MeshPrimitives { get; }

    // Null when the model has no skin or its first skin exceeds MaxSupportedJoints.
    public IReadOnlyList<AnimatedModelSkinJoint>? SkinJoints { get; }

    public IReadOnlyDictionary<string, Animation> Animations { get; }

    // The immutable rig, clips and keyframe tracks, shared by every instance of the model.
    public AnimatedModelSkeleton Skeleton { get; }

    // The rest pose the model draws when no instance is given (static models such as cubes).
    // It is never animated.
    public AnimatedModelPose Pose { get; }

    public static AnimatedModelData Load(string path)
    {
        ModelRoot model = ModelRoot.Load(path);
        AnimatedModelMaterialData[] materials = LoadMaterials(model);
        AnimatedModelSkinJoint[]? skinJoints = LoadSkinning(model);
        AnimatedModelPrimitiveData[][] meshPrimitives = LoadMeshes(model);
        Dictionary<string, Animation> animations = LoadAnimations(model);
        AnimatedModelSkeleton skeleton = new(model, animations, skinJoints);
        return new AnimatedModelData(path, model, materials, meshPrimitives, skinJoints, animations, skeleton);
    }

    internal void MarkConsumed()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"AnimatedModelData for '{SourcePath}' was already used by another AnimatedModel; load a new one per instance.");
        }
    }

    public static Vertex[] ProcessVertices(MeshPrimitive primitive)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        var texCoords0 = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
        var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

        Vertex[] vertices = new Vertex[positions.Count];
        for (int i = 0; i < vertices.Length; i++)
        {
            // Convert the texture coordinate and flip the V component:
            Vector2 uv = texCoords0 != null ? texCoords0[i].ToOpenTK() : Vector2.Zero;
            uv.Y = 1 - uv.Y;  // Flip V

            vertices[i] = new Vertex
            {
                Position = positions[i].ToOpenTK(),
                Normal = normals?[i].ToOpenTK() ?? Vector3.Zero,
                Tangent = tangents?[i].ToOpenTKTan() ?? Vector3.Zero,
                TexCoord0 = uv,
                Joints = joints?[i].ToOpenTK() ?? Vector4.Zero,
                Weights = weights?[i].ToOpenTK() ?? Vector4.Zero
            };
        }

        // Log the normals for the first few vertices.
        for (int i = 0; i < Math.Min(5, vertices.Length); i++)
        {
            Log($"Vertex {i} normal: {vertices[i].Normal}");
        }

        return vertices;
    }

    private static AnimatedModelSkinJoint[]? LoadSkinning(ModelRoot model)
    {
        if (model.LogicalSkins.Count == 0)
        {
            return null;
        }

        Skin skin = model.LogicalSkins[0];
        if (skin.JointsCount > MaxSupportedJoints)
        {
            Log($"Skipping skinning for '{skin.Name ?? "<unnamed>"}' because it uses {skin.JointsCount} joints and the current limit is {MaxSupportedJoints}.");
            return null;
        }

        AnimatedModelSkinJoint[] joints = new AnimatedModelSkinJoint[skin.JointsCount];
        for (int i = 0; i < skin.JointsCount; i++)
        {
            (Node Joint, System.Numerics.Matrix4x4 InverseBindMatrix) = skin.GetJoint(i);
            joints[i] = new AnimatedModelSkinJoint(Joint.LogicalIndex, InverseBindMatrix.ToOpenTK());
        }

        return joints;
    }

    public const string EmissiveScrollExtrasKey = "dreamEmissiveScroll";

    // Reads the material extra `dreamEmissiveScroll: [u, v]`. Absent, not an array of two
    // numbers, or non-finite gives zero (no scroll).
    public static Vector2 ReadEmissiveUvScroll(System.Text.Json.Nodes.JsonNode? extras)
    {
        if (extras is not System.Text.Json.Nodes.JsonObject obj
            || !obj.TryGetPropertyValue(EmissiveScrollExtrasKey, out System.Text.Json.Nodes.JsonNode? node)
            || node is not System.Text.Json.Nodes.JsonArray array
            || array.Count != 2)
        {
            return Vector2.Zero;
        }

        try
        {
            float u = array[0]!.GetValue<float>();
            float v = array[1]!.GetValue<float>();
            return float.IsFinite(u) && float.IsFinite(v) ? new Vector2(u, v) : Vector2.Zero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
        {
            return Vector2.Zero;
        }
    }

    private static AnimatedModelMaterialData[] LoadMaterials(ModelRoot model)
    {
        AnimatedModelMaterialData[] materials = new AnimatedModelMaterialData[model.LogicalMaterials.Count];
        foreach (var material in model.LogicalMaterials)
        {
            var materialData = new AnimatedModelMaterialData
            {
                AlphaMode = material.Alpha,
                AlphaCutoff = material.AlphaCutoff,
                BaseColorFactor = GetMaterialColor(material, "BaseColor", Vector4.One)
            };

            // Handle MetallicRoughness parameters
            var metallicRoughness = material.FindChannel("MetallicRoughness");
            if (metallicRoughness.HasValue)
            {
                // Default values according to glTF spec
                float metallic = 1.0f;
                float roughness = 1.0f;

                // Search through parameters list
                foreach (var param in metallicRoughness.Value.Parameters)
                {
                    if (param.Name == "MetallicFactor")
                    {
                        metallic = metallicRoughness.Value.GetFactor("MetallicFactor");
                    }
                    else if (param.Name == "RoughnessFactor")
                    {
                        roughness = metallicRoughness.Value.GetFactor("RoughnessFactor");
                    }
                }

                materialData.MetallicFactor = metallic;
                materialData.RoughnessFactor = roughness;
            }
            else
            {
                materialData.MetallicFactor = 0.0f;
                materialData.RoughnessFactor = 1.0f;
            }

            // Handle Emissive
            var emissive = material.FindChannel("Emissive");
            materialData.EmissiveFactor = emissive.HasValue
                ? emissive.Value.Color.ToOpenTK().Xyz
                : Vector3.Zero;

            materialData.EmissiveUvScroll = ReadEmissiveUvScroll(material.Extras);

            // Decode textures
            materialData.BaseColorTexture = DecodeTexture(material, "BaseColor");
            materialData.NormalTexture = DecodeTexture(material, "Normal");
            materialData.MetallicRoughnessTexture = DecodeTexture(material, "MetallicRoughness");
            materialData.EmissiveTexture = DecodeTexture(material, "Emissive");
            materialData.OcclusionTexture = DecodeTexture(material, "Occlusion");

            // Log material properties
            Log($"Material '{material.Name}':");
            Log($"  BaseColorFactor: {materialData.BaseColorFactor}");
            Log($"  EmissiveFactor: {materialData.EmissiveFactor}");
            Log($"  MetallicFactor: {materialData.MetallicFactor}");
            Log($"  RoughnessFactor: {materialData.RoughnessFactor}");
            Log($"  AlphaMode: {materialData.AlphaMode}, AlphaCutoff: {materialData.AlphaCutoff}");
            Log($"  Has BaseColorTexture: {materialData.BaseColorTexture != null}");
            Log($"  Has NormalTexture: {materialData.NormalTexture != null}");
            Log($"  Has MetallicRoughnessTexture: {materialData.MetallicRoughnessTexture != null}");
            Log($"  Has EmissiveTexture: {materialData.EmissiveTexture != null}");
            Log($"  Has OcclusionTexture: {materialData.OcclusionTexture != null}");

            materials[material.LogicalIndex] = materialData;
        }

        return materials;
    }

    private static Vector4 GetMaterialColor(Material material, string channelName, Vector4 defaultValue)
    {
        // Attempt to find the specified channel.
        var channelOpt = material.FindChannel(channelName);
        if (!channelOpt.HasValue)
        {
            return defaultValue;
        }

        try
        {
            // channelOpt.Value.Color returns a System.Numerics.Vector4.
            System.Numerics.Vector4 sysColor = channelOpt.Value.Color;
            // Convert to OpenTK.Mathematics.Vector4 by copying the components.
            return new Vector4(sysColor.X, sysColor.Y, sysColor.Z, sysColor.W);
        }
        catch (InvalidOperationException)
        {
            return defaultValue;
        }
    }

    private static DecodedTexture? DecodeTexture(Material material, string channelName)
    {
        var channelOpt = material.FindChannel(channelName);
        if (!channelOpt.HasValue || channelOpt.Value.Texture == null)
        {
            // No texture available on this channel.
            return null;
        }

        // Get the primary image from the channel.
        var image = channelOpt.Value.Texture.PrimaryImage;
        if (image == null)
        {
            return null;
        }

        using var stream = new MemoryStream(image.Content.Content.ToArray());
        return TextureDecoder.DecodeRgbaFlipped(stream);
    }

    private static AnimatedModelPrimitiveData[][] LoadMeshes(ModelRoot model)
    {
        Log("Loading Meshes...");
        AnimatedModelPrimitiveData[][] meshes = new AnimatedModelPrimitiveData[model.LogicalMeshes.Count][];
        foreach (var mesh in model.LogicalMeshes)
        {
            Log($"Mesh '{mesh.Name}' with {mesh.Primitives.Count} primitives:");
            var primitives = new AnimatedModelPrimitiveData[mesh.Primitives.Count];

            for (int i = 0; i < primitives.Length; i++)
            {
                MeshPrimitive primitive = mesh.Primitives[i];
                Vertex[] vertices = ProcessVertices(primitive);
                uint[] indices = primitive.GetIndices().Select(index => (uint)index).ToArray();

                Log($"  Primitive with {indices.Length} indices, material '{primitive.Material?.Name ?? "None"}'");

                primitives[i] = new AnimatedModelPrimitiveData(
                    vertices,
                    indices,
                    primitive.DrawPrimitiveType,
                    primitive.Material?.LogicalIndex ?? -1,
                    UsesSkinning:
                        primitive.GetVertexAccessor("JOINTS_0") != null &&
                        primitive.GetVertexAccessor("WEIGHTS_0") != null);
            }

            meshes[mesh.LogicalIndex] = primitives;
        }

        return meshes;
    }

    private static Dictionary<string, Animation> LoadAnimations(ModelRoot model)
    {
        Log("Loading Animations...");
        Dictionary<string, Animation> animations = new();
        foreach (var animation in model.LogicalAnimations)
        {
            Log($"Animation '{animation.Name}' with duration {animation.Duration} seconds");
            animations.Add(animation.Name, animation);
        }

        return animations;
    }

    private static void Log(string message)
    {
        if (EnableDiagnostics)
        {
            Console.WriteLine(message);
        }
    }
}

// One glTF primitive's CPU geometry. MaterialIndex is the Material.LogicalIndex, or -1 for none.
public sealed record AnimatedModelPrimitiveData(
    AnimatedModelData.Vertex[] Vertices,
    uint[] Indices,
    SharpGLTF.Schema2.PrimitiveType PrimitiveType,
    int MaterialIndex,
    bool UsesSkinning);

// Material factors plus decoded (not yet uploaded) textures; a null texture means the channel has none.
public sealed class AnimatedModelMaterialData
{
    public Vector4 BaseColorFactor;
    public Vector3 EmissiveFactor;
    // UV per second from the glTF material extra `dreamEmissiveScroll` (zero = still).
    public Vector2 EmissiveUvScroll;
    public float MetallicFactor;
    public float RoughnessFactor;
    public float AlphaCutoff;
    public AlphaMode AlphaMode;
    public DecodedTexture? BaseColorTexture;
    public DecodedTexture? NormalTexture;
    public DecodedTexture? MetallicRoughnessTexture;
    public DecodedTexture? EmissiveTexture;
    public DecodedTexture? OcclusionTexture;
}

public readonly record struct AnimatedModelSkinJoint(int NodeIndex, Matrix4 InverseBindMatrix);
