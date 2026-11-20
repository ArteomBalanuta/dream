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
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using PrimitiveType = OpenTK.Graphics.OpenGL4.PrimitiveType;
using Vector2 = OpenTK.Mathematics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;

// The shared, GPU-side half of an animated model: one upload of its meshes, textures and
// materials, plus the immutable AnimatedModelSkeleton (hierarchy, rest pose, clips, skin). It
// holds no animation state. Each animated actor owns an AnimatedModelInstance (CreateInstance)
// and draws it with RenderPrepared(shader, instance, ...), so actors that share a model never
// share a clip, a clip time or a pose. Without an instance the model draws its rest pose, which
// is what the static cube models use.
//
// Ownership: the model owns every GL object it creates and is disposed once, by the
// ModelAssets loader that uploaded it; instances own no GL resources.
public class AnimatedModel : IDisposable
{
    private const int MaxSupportedJoints = AnimatedModelData.MaxSupportedJoints;
    // Matches `uniform mat4 jointMatrices[64]` in cubeVertex.glsl. Built once so skinning
    // never formats uniform names per draw.
    private static readonly string[] JointMatrixUniformNames = CreateJointMatrixUniformNames();
    // The array's base location: the whole joint array is uploaded from it in one call.
    private static readonly string[] JointMatrixArrayUniformName = [JointMatrixUniformNames[0]];

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
    private readonly List<int> _textures = new();
    private bool _disposed;
    private readonly ReusableMatrixBuffer _instanceSourceMatrices = new();
    private readonly ReusableMatrixBuffer _instanceNodeMatrices = new();
    // One persistent instance VBO per model, reused by every instanced batch of it.
    private readonly InstanceMatrixBuffer _instanceMatrixBuffer = new();
    private readonly AnimatedModelSkeleton _skeleton;
    // Added to 1 and multiplied into the base colour by the enemy fragment shader (uActorTintOffset).
    // Written to the MASK program on a switch; the caller sets the base program's own uniform. Zero
    // is today's look. The caller resets it to zero after drawing a tinted actor.
    public Vector3 ActorTintOffset { get; set; }

    // The world hover's rim light (uActorRim / uActorRimEye in enemyFragment.glsl), written to a switched
    // program exactly like ActorTintOffset. Zero is off. The caller zeroes it after drawing a rimmed actor.
    public Vector4 ActorRim { get; set; }
    public Vector3 ActorRimEye { get; set; }

    // The plant sway (uWind in cubeVertex.glsl, see Rendering/Ground/GroundWind): written to every program a
    // draw of this model binds, so the MASK variant never keeps a value an earlier draw left in it. Zero (the
    // default) is no sway; GroundScatterRenderer sets it per asset and zeroes it after.
    public Vector2 Wind { get; set; }

    // The view and projection of the current BeginRender, for a switch to an alpha-mask program.
    private Matrix4 _renderView = Matrix4.Identity;
    private Matrix4 _renderProjection = Matrix4.Identity;
    // True between a depthOnly BeginRender and its EndRender (see BeginRender).
    private bool _depthOnly;
    // The rest pose, drawn when no instance is given; never animated.
    private readonly AnimatedModelPose _pose;
    // Joint matrices of the rest pose, filled per skinned node when no instance is given.
    private readonly Matrix4[] _restJointMatrices;
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
        _skeleton = data.Skeleton;
        _pose = data.Pose;
        _restJointMatrices = new Matrix4[_skeleton.JointCount];
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

    // Shared by every instance of this model; read-only.
    public AnimatedModelSkeleton Skeleton => _skeleton;

    public static string GetJointMatrixUniformName(int jointIndex) => JointMatrixUniformNames[jointIndex];

    // A new per-actor animation state over this model's skeleton. Allocates; call it when the
    // actor appears (or take one from a pool), never per frame.
    public AnimatedModelInstance CreateInstance() => new(_skeleton);

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
                EmissiveUvScroll = source.EmissiveUvScroll,
                BaseColorTexture = UploadTexture(source.BaseColorTexture),
                EmissiveTexture = UploadTexture(source.EmissiveTexture)
            };

            // Set texture presence flags
            materialData.HasBaseColorTexture = materialData.BaseColorTexture != -1;
            materialData.HasEmissiveTexture = materialData.EmissiveTexture != -1;

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

    // A one-triangle mesh uploaded through CreateBuffers, the very path every model takes, so its VAO carries the
    // shipped skinned vertex layout (position, normal, tangent, texcoord0, joints, weights at locations 0..5) without
    // restating it. Only the shader warm-up draws it: a driver that specialises a vertex shader on the attribute
    // layout at the first draw needs a draw with this layout, and with the instance attributes added, to do it early.
    internal static WarmUpMesh CreateWarmUpMesh()
    {
        AnimatedModelData.Vertex[] vertices =
        [
            new() { Position = new Vector3(-0.5f, -0.5f, 0f), Normal = Vector3.UnitZ, Tangent = Vector3.UnitX, TexCoord0 = new Vector2(0f, 0f), Weights = new Vector4(1f, 0f, 0f, 0f) },
            new() { Position = new Vector3(0.5f, -0.5f, 0f), Normal = Vector3.UnitZ, Tangent = Vector3.UnitX, TexCoord0 = new Vector2(1f, 0f), Weights = new Vector4(1f, 0f, 0f, 0f) },
            new() { Position = new Vector3(0f, 0.5f, 0f), Normal = Vector3.UnitZ, Tangent = Vector3.UnitX, TexCoord0 = new Vector2(0.5f, 1f), Weights = new Vector4(1f, 0f, 0f, 0f) }
        ];
        MeshBuffers buffers = CreateBuffers(new AnimatedModelPrimitiveData(
            vertices, [0u, 1u, 2u], SharpGLTF.Schema2.PrimitiveType.TRIANGLES, 0, false));
        return new WarmUpMesh(buffers.VAO, buffers.VBO, buffers.EBO, buffers.IndexCount, buffers.PrimitiveType);
    }

    // Named Release, not Dispose, because source-reading tests locate the model's own disposal in this file by text.
    internal sealed class WarmUpMesh
    {
        private readonly int _vao;
        private readonly int _vbo;
        private readonly int _ebo;
        private readonly int _indexCount;
        private readonly PrimitiveType _primitiveType;
        private bool _disposed;

        internal WarmUpMesh(int vao, int vbo, int ebo, int indexCount, PrimitiveType primitiveType)
        {
            _vao = vao;
            _vbo = vbo;
            _ebo = ebo;
            _indexCount = indexCount;
            _primitiveType = primitiveType;
        }

        // The draw RenderPose makes, or with `instances` the one RenderPreparedInstanced makes.
        internal void Draw(InstanceMatrixBuffer? instances)
        {
            GL.BindVertexArray(_vao);
            if (instances == null)
            {
                GL.DrawElements(_primitiveType, _indexCount, DrawElementsType.UnsignedInt, 0);
                RenderCounters.CountDraw();
            }
            else
            {
                instances.BindAttributes();
                GL.DrawElementsInstanced(_primitiveType, _indexCount, DrawElementsType.UnsignedInt, IntPtr.Zero, 1);
                RenderCounters.CountDraw();
            }

            GL.BindVertexArray(0);
        }

        internal void Release()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            GL.DeleteVertexArray(_vao);
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_ebo);
        }
    }
    #endregion

    #region Rendering
    public void Render(Shader shader, Matrix4 view, Matrix4 projection, Matrix4 modelMatrix)
    {
        BeginRender(shader, view, projection);
        RenderPrepared(shader, modelMatrix);
        EndRender();
    }

    // The shadow caster pass's BeginRender (Rendering/Shadows): culling off (chunk meshes are open shells and flora is double
    // sided), and the draws feed a depth-only program, so the per-primitive material uploads and texture binds are skipped
    // and MASK primitives are not drawn at all (the shadowDepth MASK variant discards every fragment, so drawing them would
    // only cost a call). The mode lasts until EndRender, and every BeginRender starts by clearing it, so a visible draw can
    // never inherit it.
    public void BeginDepthRender(Shader shader, Matrix4 view, Matrix4 projection)
    {
        BeginRender(shader, view, projection, enableCulling: false);
        _depthOnly = true;
    }

    public void BeginRender(Shader shader, Matrix4 view, Matrix4 projection, bool enableCulling = true)
    {
        ThrowIfDisposed();
        _depthOnly = false;
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
        // Kept for a switch to the program's alpha-mask variant in the middle of this render.
        _renderView = view;
        _renderProjection = projection;
    }

    // Draws the rest pose.
    public void RenderPrepared(
        Shader shader,
        Matrix4 modelMatrix,
        IReadOnlyDictionary<string, Matrix4>? nodeOverrides = null)
    {
        ThrowIfDisposed();
        RenderPose(shader, _pose, _restJointMatrices, modelMatrix, nodeOverrides);
    }

    // Draws one actor's instance, which must have been created from this model's skeleton.
    // The instance is only read; its owner advances it with Update once per frame.
    public void RenderPrepared(
        Shader shader,
        AnimatedModelInstance instance,
        Matrix4 modelMatrix,
        IReadOnlyDictionary<string, Matrix4>? nodeOverrides = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(instance);
        if (!ReferenceEquals(instance.Skeleton, _skeleton))
        {
            throw new ArgumentException($"The instance belongs to another model than '{SourcePath}'.", nameof(instance));
        }

        RenderPose(shader, instance.Pose, instance.JointMatrixStorage, modelMatrix, nodeOverrides);
    }

    private void RenderPose(
        Shader shader,
        AnimatedModelPose pose,
        Matrix4[] jointMatrices,
        Matrix4 modelMatrix,
        IReadOnlyDictionary<string, Matrix4>? nodeOverrides)
    {
        for (int nodeIndex = 0; nodeIndex < _renderablePrimitives.Length; nodeIndex++)
        {
            List<MeshBuffers>? primitives = _renderablePrimitives[nodeIndex];
            if (primitives == null)
                continue;

            Node node = pose.GetRenderableNode(nodeIndex);
            var nodeWorldMatrix = pose.GetRenderableNodeWorldMatrix(nodeIndex).ToOpenTK();
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
            RenderNode(primitives, combinedMatrix, shader, pose, jointMatrices, nodeWorldMatrix);
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
        _depthOnly = false;
        GL.BindVertexArray(0);
    }


    private void RenderNode(
        List<MeshBuffers> primitives,
        Matrix4 modelMatrix,
        Shader shader,
        AnimatedModelPose pose,
        Matrix4[] jointMatrices,
        Matrix4 meshWorldMatrix)
    {
        // The joint matrices depend only on the node, so they are composed once per node, and so
        // are the model and normal matrices, which every primitive of the node shares.
        bool jointsComposed = false;
        JointPaletteUploads uploaded = default;
        shader.SetModelMatrix(modelMatrix);
        Shader bound = shader;
        foreach (var primitive in primitives)
        {
            MaterialData material = ResolveMaterial(primitive.MaterialIndex);
            if (_depthOnly && UsesAlphaMaskProgram(material.AlphaMode))
            {
                continue;
            }

            Shader program = SelectProgram(shader, material);
            if (!ReferenceEquals(program, bound))
            {
                BindProgram(program, instanced: false, modelMatrix);
                bound = program;
            }

            ApplySkinning(program, primitive, pose, jointMatrices, meshWorldMatrix, ref jointsComposed, ref uploaded);

            if (!_depthOnly)
            {
                SetMaterialProperties(program, material);
            }

            GL.BindVertexArray(primitive.VAO);
            GL.DrawElements(primitive.PrimitiveType, primitive.IndexCount,
                DrawElementsType.UnsignedInt, 0);
            RenderCounters.CountDraw();
        }

        RestoreProgram(shader, bound);
    }

    private void RenderNodeInstanced(
        List<MeshBuffers> primitives,
        Matrix4[] modelMatrices,
        int count,
        Shader shader,
        Matrix4 meshWorldMatrix)
    {
        bool jointsComposed = false;
        JointPaletteUploads uploaded = default;
        // Instanced draws read the model matrix from the per-instance attribute and derive the
        // normal matrix from it in the shader, so `model` and `normalMatrix` are not uploaded.
        shader.SetBool("useInstancing", true);
        // Every primitive of the node draws the same instances, so their matrices go up once per
        // node; each primitive only points its own VAO at the shared buffer.
        _instanceMatrixBuffer.Upload(modelMatrices, count);
        Shader bound = shader;
        foreach (MeshBuffers primitive in primitives)
        {
            MaterialData material = ResolveMaterial(primitive.MaterialIndex);
            if (_depthOnly && UsesAlphaMaskProgram(material.AlphaMode))
            {
                continue;
            }

            Shader program = SelectProgram(shader, material);
            if (!ReferenceEquals(program, bound))
            {
                BindProgram(program, instanced: true, Matrix4.Identity);
                bound = program;
            }

            ApplySkinning(program, primitive, _pose, _restJointMatrices, meshWorldMatrix, ref jointsComposed, ref uploaded);

            if (!_depthOnly)
            {
                SetMaterialProperties(program, material);
            }

            GL.BindVertexArray(primitive.VAO);
            _instanceMatrixBuffer.BindAttributes();
            GL.DrawElementsInstanced(
                primitive.PrimitiveType,
                primitive.IndexCount,
                DrawElementsType.UnsignedInt,
                IntPtr.Zero,
                count);
            // One instanced call counts as one draw.
            RenderCounters.CountDraw();
        }

        RestoreProgram(shader, bound);
    }

    // MASK materials draw with the program's alpha-mask variant (the only program with the cutoff
    // discard); everything else, and any program without a variant, draws with the base program.
    private static Shader SelectProgram(Shader shader, MaterialData material) =>
        UsesAlphaMaskProgram(material.AlphaMode) && shader.AlphaMaskVariant is { } maskProgram
            ? maskProgram
            : shader;

    public static bool UsesAlphaMaskProgram(AlphaMode alphaMode) =>
        ToWorldAlphaMode(alphaMode) == WorldAlphaMode.Mask;

    // Switches programs in the middle of a node: the per-render state BeginRender gave the base
    // program is uploaded to the new one. Skinning flags, joints and material follow per primitive.
    private void BindProgram(Shader program, bool instanced, Matrix4 modelMatrix)
    {
        program.Use();
        program.SetMatrix4("view", _renderView);
        program.SetMatrix4("projection", _renderProjection);
        program.SetBool("useInstancing", instanced);
        // Wave I: the enemy shader's colour tint (zero for every other model), so the MASK variant
        // never draws with a value an earlier frame or model left in it.
        program.SetVector3("uActorTintOffset", ActorTintOffset);
        program.SetVector4("uActorRim", ActorRim);
        program.SetVector3("uActorRimEye", ActorRimEye);
        // The equipment material response (enemyFragment.glsl, uEquipSurface / uEquipEye): zero for
        // every model AnimatedModel draws, so a MASK variant never answers light as a sculpted
        // pauldron because an equipment draw left its surface in that program object. The eye is
        // unread while the surface is zero and is written anyway, so no uniform of the variant is
        // ever stale (Wave9AlphaMaskUniformCoverageTests).
        program.SetEquipmentSurface(Vector4.Zero);
        program.SetEquipmentEye(Vector3.Zero);
        program.SetEquipmentDetail(Vector4.Zero);
        program.SetVector2("uWind", Wind);
        if (!instanced)
        {
            program.SetModelMatrix(modelMatrix);
        }
    }

    // Callers keep drawing with the program they passed in, so it is rebound after a switch.
    private static void RestoreProgram(Shader shader, Shader bound)
    {
        if (!ReferenceEquals(bound, shader))
        {
            shader.Use();
        }
    }

    private void ApplySkinning(
        Shader shader,
        MeshBuffers primitive,
        AnimatedModelPose pose,
        Matrix4[] jointMatrices,
        Matrix4 meshWorldMatrix,
        ref bool jointsComposed,
        ref JointPaletteUploads uploaded)
    {
        if (!primitive.UsesSkinning || !_skeleton.HasSkin)
        {
            shader.SetBool("useSkinning", false);
            return;
        }

        shader.SetBool("useSkinning", true);
        if (!jointsComposed)
        {
            AnimatedModelInstance.ComputeJointMatrices(_skeleton, pose, meshWorldMatrix, jointMatrices);
            jointsComposed = true;
        }

        // The palette is the same for every primitive of the node, so each program object (the base
        // one and the MASK variant) receives it once per node call.
        if (uploaded.TryMark(shader))
        {
            UploadJointMatrices(shader.GetUniformLocations(JointMatrixArrayUniformName)[0], jointMatrices);
        }
    }

    // Which program objects already hold the current node's joint palette: a node draws with at
    // most two (base and MASK variant), so two slots, no allocation. A fresh record per node call.
    internal struct JointPaletteUploads
    {
        private object? _first;
        private object? _second;

        // True when the program has not received the palette yet (and is now recorded as having it).
        public bool TryMark(object program)
        {
            if (ReferenceEquals(_first, program) || ReferenceEquals(_second, program))
            {
                return false;
            }

            if (_first == null)
            {
                _first = program;
            }
            else
            {
                _second = program;
            }

            return true;
        }
    }

    // The whole joint array in one glUniformMatrix4fv call (one counted uniform upload), from the
    // caller's own array, so a skinned draw neither loops over joint uniforms nor allocates.
    private static void UploadJointMatrices(int location, Matrix4[] jointMatrices)
    {
        if (jointMatrices.Length == 0)
        {
            return;
        }

        ReadOnlySpan<float> values = MemoryMarshal.Cast<Matrix4, float>(jointMatrices.AsSpan());
        GL.UniformMatrix4(location, jointMatrices.Length, false, ref MemoryMarshal.GetReference(values));
        RenderCounters.CountUniformUpload();
    }

    private void SetMaterialProperties(Shader shader, MaterialData material)
    {
        shader.SetVector4("material.baseColorFactor", material.BaseColorFactor);
        shader.SetFloat("material.metallicFactor", material.MetallicFactor);
        shader.SetFloat("material.roughnessFactor", material.RoughnessFactor);
        shader.SetEmissiveFactor(material.EmissiveFactor);
        // Only the alpha-mask program declares the cutoff; elsewhere this uploads nothing.
        shader.SetAlphaCutoff(material.AlphaCutoff);

        // Set texture presence flags
        shader.SetBool("material.hasBaseColorTexture", material.HasBaseColorTexture);
        shader.SetHasEmissiveTexture(material.HasEmissiveTexture);
        shader.SetEmissiveUvScroll(material.EmissiveUvScroll);
        if (material.EmissiveUvScroll != Vector2.Zero)
        {
            shader.SetTime(RenderFrameClock.Seconds);
        }

        // Bind textures
        SetTexture(shader, "material.baseColorTexture", 0, material.BaseColorTexture);
        SetTexture(shader, "material.emissiveTexture", 3, material.EmissiveTexture);
    }


    // Maps a glTF alpha mode onto the world pass's. Blending is off there and there is no sorted
    // transparency, so BLEND (like OPAQUE) is drawn opaque; only MASK uses the alpha-mask program.
    public static int ToWorldAlphaMode(AlphaMode alphaMode) => alphaMode == AlphaMode.MASK
        ? WorldAlphaMode.Mask
        : WorldAlphaMode.Opaque;

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
        _instanceMatrixBuffer.Dispose();

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
    private class MaterialData
    {
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor;
        public Vector2 EmissiveUvScroll;
        public float MetallicFactor;
        public float RoughnessFactor;
        public float AlphaCutoff;
        public AlphaMode AlphaMode;
        public int BaseColorTexture = -1;
        public int EmissiveTexture = -1;

        // Add texture presence flags
        public bool HasBaseColorTexture;
        public bool HasEmissiveTexture;
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
