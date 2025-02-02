using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using PrimitiveType = OpenTK.Graphics.OpenGL4.PrimitiveType;

public class AnimatedModel
{
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

    private void InitializeModel()
    {
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
                // GL.EnableVertexAttribPointer(2);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, Vertex.Size, 6 * sizeof(float));

                GL.BindVertexArray(0);
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

        foreach (var node in _model.DefaultScene.VisualChildren)
        {
            if (node.Mesh == null || !_meshPrimitives.TryGetValue(node.Mesh, out var primitives)) continue;

            foreach (var meshBuffers in primitives)
            {
                GL.BindVertexArray(meshBuffers.VAO);

                // Combine base model matrix with node's transform
                var modelMatrix = baseModel * node.WorldMatrix.ToOpenTKMatrix();

                GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "model"), false, ref modelMatrix);
                GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "view"), false, ref view);
                GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "projection"), false, ref projection);

                GL.DrawElements(PrimitiveType.Triangles, meshBuffers.IndexCount, DrawElementsType.UnsignedInt, 0);
                GL.BindVertexArray(0);
            }
        }
    }

    public void StartAnimation(string name = null) => _isAnimating = true;
    public void StopAnimation() => _isAnimating = false;

    private struct MeshBuffers
    {
        public int VAO;
        public int VBO;
        public int EBO;
        public int IndexCount;
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