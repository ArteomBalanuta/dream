using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices;
using Image = SixLabors.ImageSharp.Image;
using PrimitiveType = OpenTK.Graphics.OpenGL4.PrimitiveType;
using Vector2 = OpenTK.Mathematics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;

public class AnimatedModel : IDisposable
{

    #region Structures
    private struct MeshBuffers
    {
        public int VAO;
        public int VBO;
        public int EBO;
        public int IndexCount;
        public Material Material;
        public PrimitiveType PrimitiveType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;
        public Vector2 TexCoord0;
        public Vector4 Joints;
        public Vector4 Weights;

        public const int Size = (3 + 3 + 3 + 2 + 4 + 4) * sizeof(float);
    }
    #endregion

    private readonly ModelRoot _model;
    private readonly Dictionary<Material, MaterialData> _materials = new();
    private readonly Dictionary<Mesh, List<MeshBuffers>> _meshPrimitives = new();
    private readonly Dictionary<string, Animation> _animations = new();
    private readonly List<int> _textures = new();
    private float _animationTime;
    private bool _isAnimating;

    public AnimatedModel(string filePath)
    {
        _model = ModelRoot.Load(filePath);
        Initialize();
    }

    #region Initialization
    private void Initialize()
    {
        LoadMaterials();
        LoadMeshes();
        LoadAnimations();
    }
    private void LoadMaterials()
    {
        foreach (var material in _model.LogicalMaterials)
        {
            var materialData = new MaterialData
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

            // Load textures
            materialData.BaseColorTexture = LoadTexture(material, "BaseColor");
            materialData.NormalTexture = LoadTexture(material, "Normal");
            materialData.MetallicRoughnessTexture = LoadTexture(material, "MetallicRoughness");
            materialData.EmissiveTexture = LoadTexture(material, "Emissive");
            materialData.OcclusionTexture = LoadTexture(material, "Occlusion");

            // Set texture presence flags
            materialData.HasBaseColorTexture = materialData.BaseColorTexture != -1;
            materialData.HasNormalTexture = materialData.NormalTexture != -1;
            materialData.HasMetallicRoughnessTexture = materialData.MetallicRoughnessTexture != -1;
            materialData.HasEmissiveTexture = materialData.EmissiveTexture != -1;
            materialData.HasOcclusionTexture = materialData.OcclusionTexture != -1;

            // Log material properties
            Console.WriteLine($"Material '{material.Name}':");
            Console.WriteLine($"  BaseColorFactor: {materialData.BaseColorFactor}");
            Console.WriteLine($"  EmissiveFactor: {materialData.EmissiveFactor}");
            Console.WriteLine($"  MetallicFactor: {materialData.MetallicFactor}");
            Console.WriteLine($"  RoughnessFactor: {materialData.RoughnessFactor}");
            Console.WriteLine($"  AlphaMode: {materialData.AlphaMode}, AlphaCutoff: {materialData.AlphaCutoff}");
            Console.WriteLine($"  Has BaseColorTexture: {materialData.HasBaseColorTexture}");
            Console.WriteLine($"  Has NormalTexture: {materialData.HasNormalTexture}");
            Console.WriteLine($"  Has MetallicRoughnessTexture: {materialData.HasMetallicRoughnessTexture}");
            Console.WriteLine($"  Has EmissiveTexture: {materialData.HasEmissiveTexture}");
            Console.WriteLine($"  Has OcclusionTexture: {materialData.HasOcclusionTexture}");


            _materials.Add(material, materialData);
        }
    }

    private OpenTK.Mathematics.Vector4 GetMaterialColor(Material material, string channelName, OpenTK.Mathematics.Vector4 defaultValue)
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
            return new OpenTK.Mathematics.Vector4(sysColor.X, sysColor.Y, sysColor.Z, sysColor.W);
        }
        catch (InvalidOperationException)
        {
            return defaultValue;
        }
    }



    private float GetMaterialParameter(Material material, string channelName, string parameterName, float defaultValue)
    {
        var channel = material.FindChannel(channelName);
        if (!channel.HasValue) return defaultValue;

        // Use the MaterialChannel's GetFactor method to retrieve a float parameter.
        // For example, by convention: MetallicFactor is stored in channel.Factor.X and RoughnessFactor in channel.Factor.Y.
        try
        {
            return channel.Value.GetFactor(parameterName);
        }
        catch
        {
            return defaultValue;
        }
    }

    private int LoadTexture(Material material, string channelName)
    {
        var channelOpt = material.FindChannel(channelName);
        if (!channelOpt.HasValue || channelOpt.Value.Texture == null)
        {
            // No texture available on this channel.
            return -1;
        }

        // Get the primary image from the channel.
        var image = channelOpt.Value.Texture.PrimaryImage;
        if (image == null)
        {
            return -1;
        }

        // Create an OpenGL texture.
        int handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, handle);

        // Load image data from the image's content stream.
        using (var stream = new MemoryStream(image.Content.Content.ToArray()))
        using (var img = Image.Load<Rgba32>(stream))
        {
            // Flip the image vertically to match OpenGL coordinate system.
            img.Mutate(x => x.Flip(FlipMode.Vertical));

            // Allocate a buffer and copy pixel data.
            byte[] pixelData = new byte[4 * img.Width * img.Height];
            img.CopyPixelDataTo(pixelData);

            // Upload the texture data to OpenGL.
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                img.Width, img.Height, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, pixelData);

            // Set texture filtering and generate mipmaps.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }

        // Optionally add the texture handle to a collection to manage its lifetime.
        _textures.Add(handle);

        return handle;
    }


    private void LoadMeshes()
    {
        Console.WriteLine("Loading Meshes...");
        foreach (var mesh in _model.LogicalMeshes)
        {
            Console.WriteLine($"Mesh '{mesh.Name}' with {mesh.Primitives.Count} primitives:");
            var primitives = new List<MeshBuffers>();

            foreach (var primitive in mesh.Primitives)
            {
                var vertices = ProcessVertices(primitive);
                var indices = primitive.GetIndices().Select(i => (uint)i).ToArray();
                var buffers = CreateBuffers(vertices, indices, primitive);

                Console.WriteLine($"  Primitive with {indices.Length} indices, material '{primitive.Material?.Name ?? "None"}'");

                primitives.Add(buffers);
            }

            _meshPrimitives.Add(mesh, primitives);
        }
    }

    private Vertex[] ProcessVertices(MeshPrimitive primitive)
    {
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        var texCoords0 = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
        var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

        var vertices = Enumerable.Range(0, positions.Count).Select(i => new Vertex
        {
            Position = positions[i].ToOpenTK(),
            Normal = normals?[i].ToOpenTK() ?? Vector3.Zero,
            Tangent = tangents?[i].ToOpenTKTan() ?? Vector3.Zero,
            TexCoord0 = texCoords0?[i].ToOpenTK() ?? Vector2.Zero,
            Joints = joints?[i].ToOpenTK() ?? Vector4.Zero,
            Weights = weights?[i].ToOpenTK() ?? Vector4.Zero
        }).ToArray();

        // Log the normals for the first few vertices.
        for (int i = 0; i < Math.Min(5, vertices.Length); i++)
        {
            Console.WriteLine($"Vertex {i} normal: {vertices[i].Normal}");
        }

        return vertices;
    }

    private MeshBuffers CreateBuffers(Vertex[] vertices, uint[] indices, MeshPrimitive primitive)
    {
        var vao = GL.GenVertexArray();
        var vbo = GL.GenBuffer();
        var ebo = GL.GenBuffer();

        GL.BindVertexArray(vao);

        // Vertex Buffer
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * Vertex.Size, vertices, BufferUsageHint.StaticDraw);

        // Attributes
        GL.EnableVertexAttribArray(0); // Position
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.Size, 0);

        GL.EnableVertexAttribArray(1); // Normal
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, Vertex.Size, 3 * sizeof(float));

        GL.EnableVertexAttribArray(2); // Tangent
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, Vertex.Size, 6 * sizeof(float));

        GL.EnableVertexAttribArray(3); // TexCoord0
        GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, Vertex.Size, 9 * sizeof(float));

        GL.EnableVertexAttribArray(4); // Joints
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, Vertex.Size, 11 * sizeof(float));

        GL.EnableVertexAttribArray(5); // Weights
        GL.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, Vertex.Size, 15 * sizeof(float));

        // Element Buffer
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        GL.BindVertexArray(0);

        return new MeshBuffers
        {
            VAO = vao,
            VBO = vbo,
            EBO = ebo,
            IndexCount = indices.Length,
            Material = primitive.Material,
            PrimitiveType = GetPrimitiveType(primitive.DrawPrimitiveType)
        };
    }
    #endregion

    #region Animation
    private void LoadAnimations()
    {
        Console.WriteLine("Loading Animations...");
        foreach (var animation in _model.LogicalAnimations)
        {
            Console.WriteLine($"Animation '{animation.Name}' with duration {animation.Duration} seconds");
            _animations.Add(animation.Name, animation);
        }
    }

    public void Update(float deltaTime)
    {
        if (!_isAnimating) return;

        _animationTime += deltaTime * AnimationSpeed;

        foreach (var animation in _animations.Values)
        {
            var time = _animationTime % animation.Duration;
            UpdateNodes(animation, time);
        }
    }

    private void UpdateNodes(Animation animation, float time)
    {
        foreach (var node in _model.LogicalNodes)
        {
            if (!animation.FindChannels(node).Any()) continue;
            node.LocalTransform = node.GetLocalTransform(animation, time);
        }
    }
    #endregion

    #region Rendering
    public void Render(Shader shader, Matrix4 view, Matrix4 projection, Matrix4 modelMatrix)
    {
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);

        shader.Use();
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", projection);

        foreach (var node in _model.DefaultScene.VisualChildren)
        {
            if (!_meshPrimitives.TryGetValue(node.Mesh, out var primitives))
                continue;

            var nodeWorldMatrix = node.WorldMatrix.ToOpenTK();
            var combinedMatrix = modelMatrix * nodeWorldMatrix;
            /*
            Console.WriteLine($"Rendering node '{node.Name}'");
            Console.WriteLine($"  Node World Matrix: {nodeWorldMatrix}");
            Console.WriteLine($"  Combined Model Matrix: {combinedMatrix}");
            */
            RenderNode(primitives, combinedMatrix, shader);
        }

        GL.BindVertexArray(0);
    }


    private void RenderNode(List<MeshBuffers> primitives, Matrix4 modelMatrix, Shader shader)
    {
        foreach (var primitive in primitives)
        {
            shader.SetMatrix4("model", modelMatrix);

            if (_materials.TryGetValue(primitive.Material, out var material))
            {
                SetMaterialProperties(shader, material);
            }

            GL.BindVertexArray(primitive.VAO);
            GL.DrawElements(primitive.PrimitiveType, primitive.IndexCount,
                DrawElementsType.UnsignedInt, 0);
        }
    }

    private void SetMaterialProperties(Shader shader, MaterialData material)
    {
        /*
        // Log material properties for debugging
        Console.WriteLine("Setting Material Properties:");
        Console.WriteLine($"  baseColorFactor: {material.BaseColorFactor}");
        Console.WriteLine($"  metallicFactor: {material.MetallicFactor}");
        Console.WriteLine($"  roughnessFactor: {material.RoughnessFactor}");
        Console.WriteLine($"  emissiveFactor: {material.EmissiveFactor}");
        Console.WriteLine($"  alphaCutoff: {material.AlphaCutoff}");
        Console.WriteLine($"  alphaMode: {material.AlphaMode}");
        Console.WriteLine($"  hasBaseColorTexture: {material.HasBaseColorTexture}");
        Console.WriteLine($"  hasNormalTexture: {material.HasNormalTexture}");
        Console.WriteLine($"  hasMetallicRoughnessTexture: {material.HasMetallicRoughnessTexture}");
        Console.WriteLine($"  hasEmissiveTexture: {material.HasEmissiveTexture}");
        Console.WriteLine($"  hasOcclusionTexture: {material.HasOcclusionTexture}");
        */

        shader.SetVector4("material.baseColorFactor", material.BaseColorFactor);
        shader.SetFloat("material.metallicFactor", material.MetallicFactor);
        shader.SetFloat("material.roughnessFactor", material.RoughnessFactor);
        shader.SetVector3("material.emissiveFactor", material.EmissiveFactor);
        shader.SetFloat("material.alphaCutoff", material.AlphaCutoff);
        shader.SetInt("material.alphaMode", (int)material.AlphaMode);

        // Set texture presence flags
        shader.SetBool("material.hasBaseColorTexture", material.HasBaseColorTexture);
        shader.SetBool("material.hasNormalTexture", material.HasNormalTexture);
        shader.SetBool("material.hasMetallicRoughnessTexture", material.HasMetallicRoughnessTexture);
        shader.SetBool("material.hasEmissiveTexture", material.HasEmissiveTexture);
        shader.SetBool("material.hasOcclusionTexture", material.HasOcclusionTexture);

        // Bind textures
        SetTexture(shader, "material.baseColorTexture", 0, material.BaseColorTexture);
        SetTexture(shader, "material.normalTexture", 1, material.NormalTexture);
        SetTexture(shader, "material.metallicRoughnessTexture", 2, material.MetallicRoughnessTexture);
        SetTexture(shader, "material.emissiveTexture", 3, material.EmissiveTexture);
        SetTexture(shader, "material.occlusionTexture", 4, material.OcclusionTexture);
    }


    private void SetTexture(Shader shader, string name, int unit, int texture)
    {
        if (texture == -1) return;

        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        shader.SetInt(name, unit);
    }
    #endregion

    #region Helper Methods
    private PrimitiveType GetPrimitiveType(SharpGLTF.Schema2.PrimitiveType type) => type switch
    {
        SharpGLTF.Schema2.PrimitiveType.POINTS => PrimitiveType.Points,
        SharpGLTF.Schema2.PrimitiveType.LINES => PrimitiveType.Lines,
        SharpGLTF.Schema2.PrimitiveType.LINE_STRIP => PrimitiveType.LineStrip,
        SharpGLTF.Schema2.PrimitiveType.TRIANGLES => PrimitiveType.Triangles,
        SharpGLTF.Schema2.PrimitiveType.TRIANGLE_STRIP => PrimitiveType.TriangleStrip,
        _ => PrimitiveType.Triangles
    };

    public void Dispose()
    {
        foreach (var texture in _textures) GL.DeleteTexture(texture);
        foreach (var mesh in _meshPrimitives.Values.SelectMany(p => p))
        {
            GL.DeleteVertexArray(mesh.VAO);
            GL.DeleteBuffer(mesh.VBO);
            GL.DeleteBuffer(mesh.EBO);
        }
    }
    #endregion

    #region Properties
    public float AnimationSpeed { get; set; } = 1.0f;
    public void StartAnimation(string name = null) => _isAnimating = true;
    public void StopAnimation() => _isAnimating = false;

    private class MaterialData
    {
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor;
        public float MetallicFactor;
        public float RoughnessFactor;
        public float AlphaCutoff;
        public AlphaMode AlphaMode;
        public int BaseColorTexture = -1;
        public int NormalTexture = -1;
        public int MetallicRoughnessTexture = -1;
        public int EmissiveTexture = -1;
        public int OcclusionTexture = -1;

        // Add texture presence flags
        public bool HasBaseColorTexture;
        public bool HasNormalTexture;
        public bool HasMetallicRoughnessTexture;
        public bool HasEmissiveTexture;
        public bool HasOcclusionTexture;
    }
    #endregion
}

public static class GltfExtensions
{
    public static Vector3 ToOpenTK(this System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector2 ToOpenTK(this System.Numerics.Vector2 v) => new(v.X, v.Y);
    public static Vector4 ToOpenTK(this System.Numerics.Vector4 v) => new(v.X, v.Y, v.Z, v.W);
    public static Vector3 ToOpenTKTan(this System.Numerics.Vector4 v) => new(v.X, v.Y, v.Z);
    public static Matrix4 ToOpenTK(this System.Numerics.Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}