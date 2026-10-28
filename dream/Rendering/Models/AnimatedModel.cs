using Dream.Rendering.Core;
using Dream.Simulation.World;
using Dream.Utilities;

namespace Dream.Rendering.Models;

﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using PrimitiveType = OpenTK.Graphics.OpenGL4.PrimitiveType;
using Vector2 = OpenTK.Mathematics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;

public class AnimatedModel : IDisposable
{
    private const int MaxSupportedJoints = AnimatedModelData.MaxSupportedJoints;
    // Matches `uniform mat4 jointMatrices[64]` in cubeVertex.glsl. Built once so skinning
    // never formats uniform names per draw.
    private static readonly string[] JointMatrixUniformNames = CreateJointMatrixUniformNames();

    #region Structures
    private struct MeshBuffers
    {
        public int VAO;
        public int VBO;
        public int EBO;
        public int IndexCount;
        public int MaterialIndex;
        public PrimitiveType PrimitiveType;
        public bool UsesSkinning;
    }
    #endregion

    private readonly MaterialData?[] _materials;
    private readonly Dictionary<Mesh, List<MeshBuffers>> _meshPrimitives = new();
    private readonly IReadOnlyDictionary<string, Animation> _animations;
    private readonly List<int> _textures = new();
    private bool _disposed;
    private readonly ReusableMatrixBuffer _instanceSourceMatrices = new();
    private readonly ReusableMatrixBuffer _instanceNodeMatrices = new();
    private int _instanceModelBuffer;
    private AnimationPlaybackState _playback = AnimationPlaybackState.Stopped;
    private string? _idleClipName;
    private readonly AnimatedModelSkinJoint[]? _skinJoints;
    private readonly AnimatedModelPose _pose;
    private List<MeshBuffers>?[] _renderablePrimitives = [];

    public AnimatedModel(string filePath)
        : this(AnimatedModelData.Load(filePath))
    {
    }

    // Uploads already-decoded data. Render thread only; the data object must not be reused
    // for another model (see AnimatedModelData).
    public AnimatedModel(AnimatedModelData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        GLUtils.AssertRenderThread("AnimatedModel upload");
        data.MarkConsumed();

        SourcePath = data.SourcePath;
        _animations = data.Animations;
        _skinJoints = data.SkinJoints?.ToArray();
        _pose = data.Pose;
        _materials = new MaterialData?[data.Materials.Count];

        try
        {
            UploadMaterials(data);
            UploadMeshes(data);
        }
        catch
        {
            Dispose();
            throw;
        }

        LoadRenderablePrimitives();
    }

    public string SourcePath { get; }

    public static string GetJointMatrixUniformName(int jointIndex) => JointMatrixUniformNames[jointIndex];

    #region Initialization
    private void LoadRenderablePrimitives()
    {
        _renderablePrimitives = new List<MeshBuffers>?[_pose.RenderableNodeCount];
        for (int i = 0; i < _renderablePrimitives.Length; i++)
        {
            Node node = _pose.GetRenderableNode(i);
            _renderablePrimitives[i] = SafeDictionaryLookup.TryGetValue(_meshPrimitives, node.Mesh, out List<MeshBuffers> primitives)
                ? primitives
                : null;
        }
    }

    private static string[] CreateJointMatrixUniformNames()
    {
        string[] names = new string[MaxSupportedJoints];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = $"jointMatrices[{i}]";
        }

        return names;
    }

    private void UploadMaterials(AnimatedModelData data)
    {
        for (int i = 0; i < data.Materials.Count; i++)
        {
            AnimatedModelMaterialData source = data.Materials[i];
            var materialData = new MaterialData
            {
                AlphaMode = source.AlphaMode,
                AlphaCutoff = source.AlphaCutoff,
                BaseColorFactor = source.BaseColorFactor,
                MetallicFactor = source.MetallicFactor,
                RoughnessFactor = source.RoughnessFactor,
                EmissiveFactor = source.EmissiveFactor,
                BaseColorTexture = UploadTexture(source.BaseColorTexture),
                NormalTexture = UploadTexture(source.NormalTexture),
                MetallicRoughnessTexture = UploadTexture(source.MetallicRoughnessTexture),
                EmissiveTexture = UploadTexture(source.EmissiveTexture),
                OcclusionTexture = UploadTexture(source.OcclusionTexture)
            };

            // Set texture presence flags
            materialData.HasBaseColorTexture = materialData.BaseColorTexture != -1;
            materialData.HasNormalTexture = materialData.NormalTexture != -1;
            materialData.HasMetallicRoughnessTexture = materialData.MetallicRoughnessTexture != -1;
            materialData.HasEmissiveTexture = materialData.EmissiveTexture != -1;
            materialData.HasOcclusionTexture = materialData.OcclusionTexture != -1;

            _materials[i] = materialData;
        }
    }

    private int UploadTexture(DecodedTexture? texture)
    {
        if (texture == null)
        {
            // No texture available on this channel.
            return -1;
        }

        int handle = TextureDecoder.UploadTexture2D(texture, generateMipmaps: true);
        _textures.Add(handle);
        return handle;
    }

    private void UploadMeshes(AnimatedModelData data)
    {
        foreach (var mesh in data.Model.LogicalMeshes)
        {
            IReadOnlyList<AnimatedModelPrimitiveData> sourcePrimitives = data.MeshPrimitives[mesh.LogicalIndex];
            var primitives = new List<MeshBuffers>(sourcePrimitives.Count);
            // Registered before the uploads so a failed upload still disposes the buffers made so far.
            _meshPrimitives.Add(mesh, primitives);

            foreach (AnimatedModelPrimitiveData primitive in sourcePrimitives)
            {
                primitives.Add(CreateBuffers(primitive));
            }
        }
    }

    private static MeshBuffers CreateBuffers(AnimatedModelPrimitiveData primitive)
    {
        AnimatedModelData.Vertex[] vertices = primitive.Vertices;
        uint[] indices = primitive.Indices;
        const int vertexSize = AnimatedModelData.Vertex.Size;

        var vao = GL.GenVertexArray();
        var vbo = GL.GenBuffer();
        var ebo = GL.GenBuffer();

        GL.BindVertexArray(vao);

        // Vertex Buffer
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * vertexSize, vertices, BufferUsageHint.StaticDraw);

        // Attributes
        GL.EnableVertexAttribArray(0); // Position
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, vertexSize, 0);

        GL.EnableVertexAttribArray(1); // Normal
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, vertexSize, 3 * sizeof(float));

        GL.EnableVertexAttribArray(2); // Tangent
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, vertexSize, 6 * sizeof(float));

        GL.EnableVertexAttribArray(3); // TexCoord0
        GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, vertexSize, 9 * sizeof(float));

        GL.EnableVertexAttribArray(4); // Joints
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, vertexSize, 11 * sizeof(float));

        GL.EnableVertexAttribArray(5); // Weights
        GL.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, vertexSize, 15 * sizeof(float));

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
            MaterialIndex = primitive.MaterialIndex,
            PrimitiveType = GetPrimitiveType(primitive.PrimitiveType),
            UsesSkinning = primitive.UsesSkinning
        };
    }
    #endregion

    #region Animation

    public bool Update(float deltaTime)
    {
        if (!_playback.IsPlaying || string.IsNullOrWhiteSpace(_playback.ClipName))
        {
            return false;
        }

        if (!_animations.TryGetValue(_playback.ClipName, out Animation? animation))
        {
            _playback = AnimationPlaybackState.Stopped;
            return false;
        }

        _playback = _playback.Advance(deltaTime * AnimationSpeed, animation.Duration, out bool finished);
        UpdateNodes(animation, _playback.TimeSeconds);

        if (finished && _playback.Mode == AnimationPlaybackMode.OneShot && !string.IsNullOrWhiteSpace(_idleClipName))
        {
            _playback = _playback.Play(_idleClipName, AnimationPlaybackMode.Loop);
            return true;
        }

        return finished;
    }

    private void UpdateNodes(Animation animation, float time)
    {
        _pose.Apply(animation, time);
    }
    #endregion

    #region Rendering
    public void Render(Shader shader, Matrix4 view, Matrix4 projection, Matrix4 modelMatrix)
    {
        BeginRender(shader, view, projection);
        RenderPrepared(shader, modelMatrix);
        EndRender();
    }

    public void BeginRender(Shader shader, Matrix4 view, Matrix4 projection, bool enableCulling = true)
    {
        ThrowIfDisposed();
        GL.Enable(EnableCap.DepthTest);
        if (enableCulling)
        {
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);
        }
        else
        {
            GL.Disable(EnableCap.CullFace);
        }

        shader.Use();
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", projection);
        shader.SetBool("useSkinning", false);
        shader.SetBool("useInstancing", false);
    }

    public void RenderPrepared(
        Shader shader,
        Matrix4 modelMatrix,
        IReadOnlyDictionary<string, Matrix4>? nodeOverrides = null)
    {
        ThrowIfDisposed();
        for (int nodeIndex = 0; nodeIndex < _renderablePrimitives.Length; nodeIndex++)
        {
            List<MeshBuffers>? primitives = _renderablePrimitives[nodeIndex];
            if (primitives == null)
                continue;

            Node node = _pose.GetRenderableNode(nodeIndex);
            var nodeWorldMatrix = _pose.GetRenderableNodeWorldMatrix(nodeIndex).ToOpenTK();
            if (!string.IsNullOrWhiteSpace(node.Name) &&
                nodeOverrides != null &&
                nodeOverrides.TryGetValue(node.Name, out Matrix4 nodeOverride))
            {
                nodeWorldMatrix = nodeOverride * nodeWorldMatrix;
            }

            var combinedMatrix = AnimatedModelTransformComposer.ComposeInstanceNodeMatrix(modelMatrix, nodeWorldMatrix);
            /*
            Console.WriteLine($"Rendering node '{node.Name}'");
            Console.WriteLine($"  Node World Matrix: {nodeWorldMatrix}");
            Console.WriteLine($"  Combined Model Matrix: {combinedMatrix}");
            */
            RenderNode(primitives, combinedMatrix, shader, nodeWorldMatrix);
        }
    }

    public void RenderPreparedInstanced(
        Shader shader,
        IReadOnlyList<Matrix4> modelMatrices)
    {
        ThrowIfDisposed();
        if (modelMatrices.Count == 0)
        {
            return;
        }

        Matrix4[] copiedMatrices = _instanceSourceMatrices.Rent(modelMatrices.Count);
        for (int i = 0; i < modelMatrices.Count; i++)
        {
            copiedMatrices[i] = modelMatrices[i];
        }

        RenderPreparedInstanced(shader, copiedMatrices, modelMatrices.Count);
    }

    public void RenderPreparedInstanced(
        Shader shader,
        Matrix4[] modelMatrices,
        int count)
    {
        ThrowIfDisposed();
        if (count == 0)
        {
            return;
        }

        Matrix4[] instanceMatrices = _instanceNodeMatrices.Rent(count);
        for (int nodeIndex = 0; nodeIndex < _renderablePrimitives.Length; nodeIndex++)
        {
            List<MeshBuffers>? primitives = _renderablePrimitives[nodeIndex];
            if (primitives == null)
            {
                continue;
            }

            Matrix4 nodeWorldMatrix = _pose.GetRenderableNodeWorldMatrix(nodeIndex).ToOpenTK();
            for (int i = 0; i < count; i++)
            {
                instanceMatrices[i] = AnimatedModelTransformComposer.ComposeInstanceNodeMatrix(
                    modelMatrices[i],
                    nodeWorldMatrix);
            }

            RenderNodeInstanced(primitives, instanceMatrices, count, shader, nodeWorldMatrix);
        }

        shader.SetBool("useInstancing", false);
    }

    public void EndRender()
    {
        GL.BindVertexArray(0);
    }


    private void RenderNode(
        List<MeshBuffers> primitives,
        Matrix4 modelMatrix,
        Shader shader,
        Matrix4 meshWorldMatrix)
    {
        foreach (var primitive in primitives)
        {
            shader.SetMatrix4("model", modelMatrix);
            ApplySkinning(shader, primitive, meshWorldMatrix);

            SetMaterialProperties(shader, ResolveMaterial(primitive.MaterialIndex));

            GL.BindVertexArray(primitive.VAO);
            GL.DrawElements(primitive.PrimitiveType, primitive.IndexCount,
                DrawElementsType.UnsignedInt, 0);
        }
    }

    private void RenderNodeInstanced(
        List<MeshBuffers> primitives,
        Matrix4[] modelMatrices,
        int count,
        Shader shader,
        Matrix4 meshWorldMatrix)
    {
        foreach (MeshBuffers primitive in primitives)
        {
            shader.SetBool("useInstancing", true);
            shader.SetMatrix4("model", Matrix4.Identity);
            ApplySkinning(shader, primitive, meshWorldMatrix);

            SetMaterialProperties(shader, ResolveMaterial(primitive.MaterialIndex));

            GL.BindVertexArray(primitive.VAO);
            UploadInstanceModelMatrices(modelMatrices, count);
            GL.DrawElementsInstanced(
                primitive.PrimitiveType,
                primitive.IndexCount,
                DrawElementsType.UnsignedInt,
                IntPtr.Zero,
                count);
        }
    }

    private void UploadInstanceModelMatrices(Matrix4[] modelMatrices, int count)
    {
        _instanceModelBuffer = _instanceModelBuffer == 0
            ? GL.GenBuffer()
            : _instanceModelBuffer;

        GL.BindBuffer(BufferTarget.ArrayBuffer, _instanceModelBuffer);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            count * Unsafe.SizeOf<Matrix4>(),
            modelMatrices,
            BufferUsageHint.DynamicDraw);

        int stride = Unsafe.SizeOf<Matrix4>();
        for (int column = 0; column < 4; column++)
        {
            int attributeLocation = 6 + column;
            GL.EnableVertexAttribArray(attributeLocation);
            GL.VertexAttribPointer(
                attributeLocation,
                4,
                VertexAttribPointerType.Float,
                normalized: false,
                stride,
                column * 4 * sizeof(float));
            GL.VertexAttribDivisor(attributeLocation, 1);
        }
    }

    private void ApplySkinning(Shader shader, MeshBuffers primitive, Matrix4 meshWorldMatrix)
    {
        if (!primitive.UsesSkinning || _skinJoints == null)
        {
            shader.SetBool("useSkinning", false);
            return;
        }

        shader.SetBool("useSkinning", true);
        ReadOnlySpan<int> jointLocations = shader.GetUniformLocations(JointMatrixUniformNames);
        for (int i = 0; i < _skinJoints.Length; i++)
        {
            AnimatedModelSkinJoint joint = _skinJoints[i];
            Matrix4 jointMatrix = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                meshWorldMatrix,
                _pose.GetNodeWorldMatrix(joint.NodeIndex).ToOpenTK(),
                joint.InverseBindMatrix);
            shader.SetMatrix4(jointLocations[i], jointMatrix);
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
    // glTF default material for primitives without one: white base colour, no textures,
    // metallic and roughness 1, opaque. Always applied so no state leaks from the previous draw.
    private static readonly MaterialData DefaultMaterial = new()
    {
        BaseColorFactor = Vector4.One,
        EmissiveFactor = Vector3.Zero,
        MetallicFactor = 1f,
        RoughnessFactor = 1f,
        AlphaCutoff = 0.5f,
    };

    private MaterialData ResolveMaterial(int materialIndex) =>
        TryGetMaterial(materialIndex, out MaterialData? material) ? material : DefaultMaterial;

    private bool TryGetMaterial(int materialIndex, [NotNullWhen(true)] out MaterialData? material)
    {
        material = materialIndex >= 0 && materialIndex < _materials.Length
            ? _materials[materialIndex]
            : null;
        return material != null;
    }

    private static PrimitiveType GetPrimitiveType(SharpGLTF.Schema2.PrimitiveType type) => type switch
    {
        SharpGLTF.Schema2.PrimitiveType.POINTS => PrimitiveType.Points,
        SharpGLTF.Schema2.PrimitiveType.LINES => PrimitiveType.Lines,
        SharpGLTF.Schema2.PrimitiveType.LINE_STRIP => PrimitiveType.LineStrip,
        SharpGLTF.Schema2.PrimitiveType.TRIANGLES => PrimitiveType.Triangles,
        SharpGLTF.Schema2.PrimitiveType.TRIANGLE_STRIP => PrimitiveType.TriangleStrip,
        _ => PrimitiveType.Triangles
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var texture in _textures) GL.DeleteTexture(texture);
        if (_instanceModelBuffer != 0)
        {
            GL.DeleteBuffer(_instanceModelBuffer);
            _instanceModelBuffer = 0;
        }

        foreach (var mesh in _meshPrimitives.Values.SelectMany(p => p))
        {
            GL.DeleteVertexArray(mesh.VAO);
            GL.DeleteBuffer(mesh.VBO);
            GL.DeleteBuffer(mesh.EBO);
        }

        _textures.Clear();
        _meshPrimitives.Clear();
    }
    #endregion

    #region Properties
    public float AnimationSpeed { get; set; } = 1.0f;

    public void PlayLoop(string clipName)
    {
        _idleClipName = clipName;
        _playback = _playback.Play(clipName, AnimationPlaybackMode.Loop);
    }

    public void PlayOneShot(string clipName)
    {
        _playback = _playback.Play(clipName, AnimationPlaybackMode.OneShot);
    }

    public void StartAnimation(string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        PlayLoop(name);
    }

    public void StopAnimation()
    {
        _playback = AnimationPlaybackState.Stopped;
        _idleClipName = null;
    }

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

// The animated pose of one model instance: a local and a world matrix per logical node,
// and keyframe arrays per animated node, all built at load. SharpGLTF builds new curve samplers
// on every Node.GetLocalTransform(animation, time) call, its linear samplers allocate on every
// GetPoint, and Node.WorldMatrix allocates while it walks the parents. So the pose keeps its own
// copy of the hierarchy and the keyframes, and leaves the glTF nodes at their loaded state.
// It has no GL dependency.
public sealed class AnimatedModelPose
{
    private readonly Node[] _nodes;
    private readonly int[] _parentIndices;
    private readonly int[] _hierarchyOrder;
    private readonly System.Numerics.Vector3[] _scales;
    private readonly System.Numerics.Quaternion[] _rotations;
    private readonly System.Numerics.Vector3[] _translations;
    private readonly System.Numerics.Matrix4x4[] _localMatrices;
    private readonly System.Numerics.Matrix4x4[] _worldMatrices;
    private readonly Node[] _renderableNodes;
    private readonly int[] _renderableNodeIndices;
    private readonly Dictionary<Animation, AnimatedNodeTrack[]> _tracksByAnimation = new();

    public AnimatedModelPose(ModelRoot model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _nodes = model.LogicalNodes.ToArray();
        int nodeCount = _nodes.Length;
        _parentIndices = new int[nodeCount];
        _scales = new System.Numerics.Vector3[nodeCount];
        _rotations = new System.Numerics.Quaternion[nodeCount];
        _translations = new System.Numerics.Vector3[nodeCount];
        _localMatrices = new System.Numerics.Matrix4x4[nodeCount];
        _worldMatrices = new System.Numerics.Matrix4x4[nodeCount];

        for (int i = 0; i < nodeCount; i++)
        {
            Node node = _nodes[i];
            _parentIndices[i] = node.VisualParent?.LogicalIndex ?? -1;
            _localMatrices[i] = node.LocalMatrix;

            SharpGLTF.Transforms.AffineTransform localTransform = node.LocalTransform;
            if (localTransform.IsMatrix)
            {
                System.Numerics.Matrix4x4.Decompose(
                    localTransform.Matrix,
                    out _scales[i],
                    out _rotations[i],
                    out _translations[i]);
            }
            else
            {
                _scales[i] = localTransform.Scale;
                _rotations[i] = localTransform.Rotation;
                _translations[i] = localTransform.Translation;
            }
        }

        _hierarchyOrder = CreateHierarchyOrder(_parentIndices);

        List<Node> renderableNodes = [];
        SceneNodeTraversal.CollectRenderableNodes(model.DefaultScene.VisualChildren, renderableNodes);
        _renderableNodes = renderableNodes.ToArray();
        _renderableNodeIndices = new int[_renderableNodes.Length];
        for (int i = 0; i < _renderableNodes.Length; i++)
        {
            _renderableNodeIndices[i] = _renderableNodes[i].LogicalIndex;
        }

        foreach (Animation animation in model.LogicalAnimations)
        {
            _tracksByAnimation[animation] = CreateTracks(animation);
        }

        UpdateWorldMatrices();
    }

    public int NodeCount => _nodes.Length;

    public int RenderableNodeCount => _renderableNodes.Length;

    public Node GetRenderableNode(int renderableIndex) => _renderableNodes[renderableIndex];

    public System.Numerics.Matrix4x4 GetRenderableNodeWorldMatrix(int renderableIndex) =>
        _worldMatrices[_renderableNodeIndices[renderableIndex]];

    public System.Numerics.Matrix4x4 GetNodeLocalMatrix(int logicalIndex) => _localMatrices[logicalIndex];

    public System.Numerics.Matrix4x4 GetNodeWorldMatrix(int logicalIndex) => _worldMatrices[logicalIndex];

    public int GetAnimatedNodeCount(Animation animation) => GetTracks(animation).Length;

    // Same result as assigning node.GetLocalTransform(animation, time) to every node the
    // animation targets: channels the animation lacks keep the node's current value.
    public void Apply(Animation animation, float time)
    {
        AnimatedNodeTrack[] tracks = GetTracks(animation);
        for (int i = 0; i < tracks.Length; i++)
        {
            AnimatedNodeTrack track = tracks[i];
            int nodeIndex = track.NodeIndex;
            if (track.Scale != null)
            {
                _scales[nodeIndex] = track.Scale.Sample(time);
            }

            if (track.Rotation != null)
            {
                _rotations[nodeIndex] = track.Rotation.Sample(time);
            }

            if (track.Translation != null)
            {
                _translations[nodeIndex] = track.Translation.Sample(time);
            }

            _localMatrices[nodeIndex] = new SharpGLTF.Transforms.AffineTransform(
                _scales[nodeIndex],
                _rotations[nodeIndex],
                _translations[nodeIndex]).Matrix;
        }

        UpdateWorldMatrices();
    }

    private AnimatedNodeTrack[] GetTracks(Animation animation)
    {
        if (!_tracksByAnimation.TryGetValue(animation, out AnimatedNodeTrack[]? tracks))
        {
            tracks = CreateTracks(animation);
            _tracksByAnimation[animation] = tracks;
        }

        return tracks;
    }

    private AnimatedNodeTrack[] CreateTracks(Animation animation)
    {
        List<AnimatedNodeTrack> tracks = [];
        for (int i = 0; i < _nodes.Length; i++)
        {
            Node node = _nodes[i];
            if (!animation.FindChannels(node).Any())
            {
                continue;
            }

            tracks.Add(new AnimatedNodeTrack(
                i,
                KeyframeCurve<System.Numerics.Vector3>.Create(
                    animation.FindScaleChannel(node)?.GetScaleSampler(),
                    static (a, b, amount) => System.Numerics.Vector3.Lerp(a, b, amount)),
                KeyframeCurve<System.Numerics.Quaternion>.Create(
                    animation.FindRotationChannel(node)?.GetRotationSampler(),
                    static (a, b, amount) => System.Numerics.Quaternion.Slerp(a, b, amount)),
                KeyframeCurve<System.Numerics.Vector3>.Create(
                    animation.FindTranslationChannel(node)?.GetTranslationSampler(),
                    static (a, b, amount) => System.Numerics.Vector3.Lerp(a, b, amount))));
        }

        return tracks.ToArray();
    }

    private void UpdateWorldMatrices()
    {
        for (int i = 0; i < _hierarchyOrder.Length; i++)
        {
            int nodeIndex = _hierarchyOrder[i];
            int parentIndex = _parentIndices[nodeIndex];
            _worldMatrices[nodeIndex] = parentIndex < 0
                ? _localMatrices[nodeIndex]
                : _localMatrices[nodeIndex] * _worldMatrices[parentIndex];
        }
    }

    private static int[] CreateHierarchyOrder(int[] parentIndices)
    {
        int[] order = new int[parentIndices.Length];
        bool[] placed = new bool[parentIndices.Length];
        int count = 0;
        for (int i = 0; i < parentIndices.Length; i++)
        {
            count = PlaceWithAncestors(i, parentIndices, placed, order, count);
        }

        return order;
    }

    private static int PlaceWithAncestors(int nodeIndex, int[] parentIndices, bool[] placed, int[] order, int count)
    {
        if (placed[nodeIndex])
        {
            return count;
        }

        int parentIndex = parentIndices[nodeIndex];
        if (parentIndex >= 0)
        {
            count = PlaceWithAncestors(parentIndex, parentIndices, placed, order, count);
        }

        placed[nodeIndex] = true;
        order[count] = nodeIndex;
        return count + 1;
    }

    private sealed class AnimatedNodeTrack(
        int nodeIndex,
        KeyframeCurve<System.Numerics.Vector3>? scale,
        KeyframeCurve<System.Numerics.Quaternion>? rotation,
        KeyframeCurve<System.Numerics.Vector3>? translation)
    {
        public int NodeIndex { get; } = nodeIndex;
        public KeyframeCurve<System.Numerics.Vector3>? Scale { get; } = scale;
        public KeyframeCurve<System.Numerics.Quaternion>? Rotation { get; } = rotation;
        public KeyframeCurve<System.Numerics.Vector3>? Translation { get; } = translation;
    }

    // One animation channel as flat key and value arrays. LINEAR and STEP sample with a binary
    // search and no allocations. CUBICSPLINE (unused by the current assets) keeps SharpGLTF's own
    // curve sampler, which is correct but allocates per sample.
    private sealed class KeyframeCurve<T>
        where T : struct
    {
        private readonly float[] _times;
        private readonly T[] _values;
        private readonly bool _step;
        private readonly Func<T, T, float, T> _interpolate;
        private readonly SharpGLTF.Animations.ICurveSampler<T>? _cubicSampler;

        private KeyframeCurve(
            float[] times,
            T[] values,
            bool step,
            Func<T, T, float, T> interpolate,
            SharpGLTF.Animations.ICurveSampler<T>? cubicSampler)
        {
            _times = times;
            _values = values;
            _step = step;
            _interpolate = interpolate;
            _cubicSampler = cubicSampler;
        }

        public static KeyframeCurve<T>? Create(IAnimationSampler<T>? sampler, Func<T, T, float, T> interpolate)
        {
            if (sampler == null)
            {
                return null;
            }

            if (sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
            {
                return new KeyframeCurve<T>([], [], step: false, interpolate, sampler.CreateCurveSampler());
            }

            (float Key, T Value)[] keys = sampler.GetLinearKeys().ToArray();
            if (keys.Length == 0)
            {
                return null;
            }

            float[] times = new float[keys.Length];
            T[] values = new T[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                times[i] = keys[i].Key;
                values[i] = keys[i].Value;
            }

            return new KeyframeCurve<T>(
                times,
                values,
                sampler.InterpolationMode == AnimationInterpolationMode.STEP,
                interpolate,
                cubicSampler: null);
        }

        public T Sample(float time)
        {
            if (_cubicSampler != null)
            {
                return _cubicSampler.GetPoint(time);
            }

            int last = _times.Length - 1;
            if (time <= _times[0])
            {
                return _values[0];
            }

            if (time >= _times[last])
            {
                return _values[last];
            }

            // Largest key index whose time is at or before the sample time.
            int low = 0;
            int high = last;
            while (high - low > 1)
            {
                int middle = (low + high) >> 1;
                if (_times[middle] <= time)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            if (_step)
            {
                return _values[low];
            }

            float amount = (time - _times[low]) / (_times[high] - _times[low]);
            return _interpolate(_values[low], _values[high], amount);
        }
    }
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
