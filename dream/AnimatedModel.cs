using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using PrimitiveType = OpenTK.Graphics.OpenGL4.PrimitiveType;
using TextureWrapMode = OpenTK.Graphics.OpenGL4.TextureWrapMode;

public class AnimatedModel
{
    private readonly Dictionary<Material, int> _textures = new();
    private readonly ModelRoot _model;
    private readonly Dictionary<string, Animation> _animations = new();
    private readonly Dictionary<Mesh, List<MeshBuffers>> _meshPrimitives = new();
    private float _animationTime;
    private bool _isAnimating;

    public AnimatedModel(string filePath)
    {
        _model = ModelRoot.Load(filePath);
        InitializeModel();
        LoadAnimations();
    }


    private int LoadTexture(SharpGLTF.Schema2.Image image)
    {
        int handle = GL.GenTexture();
        CheckGLError("GL.GenTexture");


        GL.BindTexture(TextureTarget.Texture2D, handle);
        CheckGLError("GL.BindTexture");
        // Load the image as Rgba32 from the provided stream.
        using var memoryStream = new MemoryStream(image.Content.Content.ToArray());
        using var img = Image.Load<Rgba32>(memoryStream);
        // Flip the image vertically so that it matches OpenGL's coordinate system.
        img.Mutate(x => x.Flip(FlipMode.Vertical));

        // Allocate a byte array to hold the pixel data.
        byte[] pixelData = new byte[4 * img.Width * img.Height];

        // Use ProcessPixelRows to iterate over each row of pixels.
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                // Get the current row as a span.
                Span<Rgba32> pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < pixelRow.Length; x++)
                {
                    // Calculate the correct index into the byte array.
                    int index = (y * img.Width + x) * 4;
                    pixelData[index] = pixelRow[x].R;
                    pixelData[index + 1] = pixelRow[x].G;
                    pixelData[index + 2] = pixelRow[x].B;
                    pixelData[index + 3] = pixelRow[x].A;
                }
            }
        });

        // Upload the texture data to OpenGL.
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                      img.Width, img.Height, 0, PixelFormat.Rgba,
                      PixelType.UnsignedByte, pixelData);

        CheckGLError("GL.TexImage2D");

        // Set texture filtering and wrapping parameters.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        CheckGLError("GL.TexParameter");

        // Add debug output in LoadTexture
        Console.WriteLine($"Loaded texture: {image.Name ?? "Unnamed"}, Size: {img.Width}x{img.Height}");

        return handle;
    }

    private void CheckGLError(string context)
    {
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Console.WriteLine($"[OpenGL Error] {error} in {context}");
        }
    }

    private void InitializeModel()
    {
        // Load textures first
        Console.WriteLine("[DEBUG] Initializing model textures...");

        foreach (var material in _model.LogicalMaterials)
        {
            Console.WriteLine($"Processing material: {material.Name ?? "Unnamed"}");

            if (material.FindChannel("BaseColor") is { } channel && channel.Texture != null)
            {
                var texture = channel.Texture;
                if (texture.PrimaryImage != null)
                {
                    Console.WriteLine($" - Found texture: {texture.PrimaryImage.Name ?? "Unnamed"}");
                    try
                    {
                        _textures[material] = LoadTexture(texture.PrimaryImage);
                        Console.WriteLine($" - Texture loaded successfully (ID: {_textures[material]})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($" - Failed to load texture: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine(" - Texture has no primary image!");
                }
            }
            else
            {
                Console.WriteLine(" - No BaseColor channel or texture found");
            }
        }

        foreach (var mesh in _model.LogicalMeshes)
        {
            var primitives = new List<MeshBuffers>();
            foreach (var primitive in mesh.Primitives)
            {
                var buffers = new MeshBuffers();
                var vertices = new List<Vertex>();
                var indices = new List<uint>();

                // Get vertex data
                var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
                var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var texCoords = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

                // Convert to OpenTK vectors
                for (int i = 0; i < positions.Count; i++)
                {
                    var vertex = new Vertex
                    {
                        Position = new Vector3(positions[i].X, positions[i].Y, positions[i].Z),
                        Normal = normals != null ? new Vector3(normals[i].X, normals[i].Y, normals[i].Z) : Vector3.Zero,
                        TexCoord = texCoords != null ? new Vector2(texCoords[i].X, texCoords[i].Y) : Vector2.Zero
                    };
                    vertices.Add(vertex);
                }

                // Get index data
                indices.AddRange(primitive.GetIndices());

                // Create buffers
                buffers.VAO = GL.GenVertexArray();
                buffers.VBO = GL.GenBuffer();
                buffers.EBO = GL.GenBuffer();
                buffers.IndexCount = indices.Count;

                // Upload data
                GL.BindVertexArray(buffers.VAO);
                GL.BindBuffer(BufferTarget.ArrayBuffer, buffers.VBO);
                GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * Vertex.Size, vertices.ToArray(), BufferUsageHint.StaticDraw);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, buffers.EBO);
                GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);

                // Set vertex attributes
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.Size, 0);
                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, Vertex.Size, 3 * sizeof(float));
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, Vertex.Size, 6 * sizeof(float));

                GL.BindVertexArray(0);

                // Store material reference
                buffers.Material = primitive.Material;
                primitives.Add(buffers);
            }
            _meshPrimitives.Add(mesh, primitives);
        }
    }

    private void LoadAnimations()
    {
        foreach (var animation in _model.LogicalAnimations)
        {
            _animations.Add(animation.Name, animation);
        }
    }

    public float AnimationSpeed { get; set; } = 1.0f;

    public void Update(float deltaTime)
    {
        if (!_isAnimating) return;

        // Apply animation speed
        _animationTime += deltaTime * AnimationSpeed;

        foreach (var animation in _animations.Values)
        {
            var duration = animation.Duration;
            var time = _animationTime % duration;

            foreach (var node in _model.LogicalNodes)
            {
                if (animation.FindChannels(node).Any())
                {
                    node.LocalTransform = node.GetLocalTransform(animation, time);
                }
            }
        }
    }
    public void Render(Matrix4 view, Matrix4 projection, int shaderProgram, Matrix4 baseModel)
    {
        GL.UseProgram(shaderProgram);
        CheckGLError("GL.UseProgram");

        // Get uniform locations
        int textureLocation = GL.GetUniformLocation(shaderProgram, "texture0");
        CheckGLError("GL.GetUniformLocation-texture0");

        int modelLocation = GL.GetUniformLocation(shaderProgram, "model");
        int viewLocation = GL.GetUniformLocation(shaderProgram, "view");
        int projectionLocation = GL.GetUniformLocation(shaderProgram, "projection");

        // Set matrices
        GL.UniformMatrix4(viewLocation, false, ref view);
        GL.UniformMatrix4(projectionLocation, false, ref projection);

        foreach (var node in _model.DefaultScene.VisualChildren)
        {
            if (node.Mesh == null || !_meshPrimitives.TryGetValue(node.Mesh, out var primitives)) continue;

            foreach (var meshBuffers in primitives)
            {
                // Bind VAO first
                GL.BindVertexArray(meshBuffers.VAO);
                CheckGLError("GL.BindVertexArray");

                // Bind texture if available
                if (meshBuffers.Material != null &&
                    _textures.TryGetValue(meshBuffers.Material, out int texture))
                {
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, texture);
                    GL.Uniform1(textureLocation, 0);
                }

                // Calculate model matrix
                var modelMatrix = baseModel * node.WorldMatrix.ToOpenTKMatrix();
                GL.UniformMatrix4(modelLocation, false, ref modelMatrix);

                // Draw elements
                GL.DrawElements(PrimitiveType.Triangles,
                              meshBuffers.IndexCount,
                              DrawElementsType.UnsignedInt,
                              0);

                CheckGLError($"GL.DrawElements (count: {meshBuffers.IndexCount})");
            }
        }

        GL.BindVertexArray(0);
    }

    public void StartAnimation(string name = null) => _isAnimating = true;
    public void StopAnimation() => _isAnimating = false;

    private struct MeshBuffers
    {
        public int VAO;
        public int VBO;
        public int EBO;
        public int IndexCount;
        public Material Material;
    }

    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;
        public const int Size = (3 + 3 + 2) * sizeof(float);
    }
}

public static class MatrixExtensions
{
    public static Matrix4 ToOpenTKMatrix(this System.Numerics.Matrix4x4 matrix)
    {
        return new Matrix4(
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44
        );
    }
}