using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.World;

// GPU-6: dynamic cubes, fallback cubes and rubble blocks go out as one instanced submission per
// (material, shader) batch. There is no GL context in tests, so the drawer runs against a
// recording target that counts one RenderCounters draw per submission, exactly like Render's
// fallback path (StaticMeshBuffer.DrawInstanced); the GL side is covered by structural checks.
public class WorldInstanceBatchDrawerTests
{
    private static readonly GeometryMaterialId[] Materials =
    [
        GeometryMaterialId.Grass,
        GeometryMaterialId.Dirt,
        GeometryMaterialId.Stone,
        GeometryMaterialId.Sand,
        GeometryMaterialId.Mud,
        GeometryMaterialId.Snow
    ];

    private static readonly AnimatedModel StoneModel =
        (AnimatedModel)RuntimeHelpers.GetUninitializedObject(typeof(AnimatedModel));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(500, 3)]
    [InlineData(2000, 6)]
    public void Fallback_Cubes_Draw_Once_Per_Material(int cubeCount, int materialCount)
    {
        var planner = new WorldRenderBatchPlanner(_ => null);
        var drawer = new WorldInstanceBatchDrawer();
        var target = new RecordingTarget();
        List<Cube> cubes = CreateCubes(cubeCount, materialCount);

        RenderCounters.Reset();
        int drawn = drawer.DrawCubeBatches(target, new Camera(Vector3.Zero), planner.PlanCubeBatches(cubes));

        Assert.Equal(materialCount, drawn);
        Assert.Equal(materialCount, RenderCounters.DrawCalls);
        Assert.Equal(materialCount, target.FallbackCalls);
        Assert.Equal(0, target.ModelCalls);
        Assert.Equal(cubeCount, target.TotalInstances);
        Assert.Same(drawer.FallbackInstances, target.LastInstances);
    }

    [Fact]
    public void Fallback_Batch_Matrices_Match_The_Single_Cube_Transform()
    {
        var planner = new WorldRenderBatchPlanner(_ => null);
        var drawer = new WorldInstanceBatchDrawer();
        var target = new RecordingTarget { CaptureMatrices = true };
        List<Cube> cubes = CreateCubes(37, 1);

        drawer.DrawCubeBatches(target, new Camera(Vector3.Zero), planner.PlanCubeBatches(cubes));

        Assert.Equal(cubes.Count, target.Captured.Count);
        for (int i = 0; i < cubes.Count; i++)
        {
            Assert.Equal(CubeTransforms.CreateCenteredVisualModelMatrix(cubes[i].Position, cubes[i].Side, 1f), target.Captured[i]);
        }
    }

    [Fact]
    public void Model_And_Fallback_Batches_Each_Submit_Once()
    {
        var planner = new WorldRenderBatchPlanner(material => material == GeometryMaterialId.Stone ? StoneModel : null);
        var drawer = new WorldInstanceBatchDrawer();
        var target = new RecordingTarget();
        List<Cube> cubes = CreateCubes(300, 3);

        int drawn = drawer.DrawCubeBatches(target, new Camera(Vector3.Zero), planner.PlanCubeBatches(cubes));

        Assert.Equal(3, drawn);
        Assert.Equal(1, target.ModelCalls);
        Assert.Equal(2, target.FallbackCalls);
        Assert.Equal(300, target.TotalInstances);
    }

    [Fact]
    public void Rubble_Blocks_Draw_Once_Per_Material_With_Their_Visual_Size()
    {
        var planner = new WorldRenderBatchPlanner(_ => null);
        var drawer = new WorldInstanceBatchDrawer();
        var target = new RecordingTarget { CaptureMatrices = true };
        List<GeometryRenderBlockDescriptor> blocks = CreateBlocks(400, 4);

        RenderCounters.Reset();
        int drawn = drawer.DrawGeometryBlockBatches(target, new Camera(Vector3.Zero), planner.PlanGeometryBlockBatches(blocks));

        Assert.Equal(4, drawn);
        Assert.Equal(4, RenderCounters.DrawCalls);
        Assert.Equal(400, target.TotalInstances);
        // Batches are first-seen order by material; block i has material i % 4.
        List<GeometryRenderBlockDescriptor> ordered = blocks.OrderBy(block => (int)block.MaterialId).ThenBy(block => block.Center.X).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            Assert.Equal(
                CubeTransforms.CreateCenteredVisualModelMatrix(ordered[i].Center, ordered[i].VisualSize, 1f),
                target.Captured[i]);
        }
    }

    [Fact]
    public void Drawing_Many_Cubes_And_Blocks_Allocates_Nothing_Per_Frame()
    {
        var planner = new WorldRenderBatchPlanner(material => material == GeometryMaterialId.Stone ? StoneModel : null);
        var drawer = new WorldInstanceBatchDrawer();
        var target = new RecordingTarget();
        var camera = new Camera(Vector3.Zero);
        List<Cube> cubes = CreateCubes(2000, 6);
        List<GeometryRenderBlockDescriptor> blocks = CreateBlocks(1500, 5);

        AllocationAssert.AtMost(0, () =>
        {
            drawer.DrawCubeBatches(target, camera, planner.PlanCubeBatches(cubes));
            drawer.DrawGeometryBlockBatches(target, camera, planner.PlanGeometryBlockBatches(blocks));
        });
    }

    [Fact]
    public void Matrix_Storage_Is_Reused_And_Only_Grows()
    {
        var planner = new WorldRenderBatchPlanner(_ => null);
        var drawer = new WorldInstanceBatchDrawer();
        var target = new RecordingTarget();
        var camera = new Camera(Vector3.Zero);

        drawer.DrawCubeBatches(target, camera, planner.PlanCubeBatches(CreateCubes(600, 1)));
        int capacity = drawer.MatrixCapacity;
        Matrix4[] array = target.LastMatrices!;
        Assert.True(capacity >= 600);

        drawer.DrawCubeBatches(target, camera, planner.PlanCubeBatches(CreateCubes(20, 2)));
        Assert.Equal(capacity, drawer.MatrixCapacity);
        Assert.Same(array, target.LastMatrices);

        drawer.DrawCubeBatches(target, camera, planner.PlanCubeBatches(CreateCubes(600, 3)));
        Assert.Equal(capacity, drawer.MatrixCapacity);
        Assert.Same(array, target.LastMatrices);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 16)]
    [InlineData(0, 40, 40)]
    [InlineData(16, 10, 16)]
    [InlineData(16, 17, 32)]
    [InlineData(32, 100, 100)]
    [InlineData(100, 100, 100)]
    public void Instance_Buffer_Capacity_Grows_Only(int current, int required, int expected)
    {
        Assert.Equal(expected, InstanceMatrixBuffer.ComputeCapacity(current, required));
    }

    [Fact]
    public void Instance_Buffer_Capacity_Never_Shrinks_Across_A_Sequence()
    {
        int capacity = 0;
        int growths = 0;
        foreach (int required in new[] { 5, 3, 40, 12, 41, 1000, 2, 999, 1001 })
        {
            int next = InstanceMatrixBuffer.ComputeCapacity(capacity, required);
            Assert.True(next >= capacity);
            Assert.True(next >= required);
            growths += next != capacity ? 1 : 0;
            capacity = next;
        }

        Assert.Equal(5, growths);
    }

    [Fact]
    public void Instance_Buffer_Orphans_And_Writes_Without_Allocating_Or_Shrinking()
    {
        string source = Read("dream/Rendering/Core/InstanceMatrixBuffer.cs");
        string upload = Method(source, "public void Upload(");

        Assert.Contains("GL.BufferData(BufferTarget.ArrayBuffer, _capacity * MatrixStride, IntPtr.Zero,", upload);
        Assert.Contains("GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, count * MatrixStride, matrices);", upload);
        Assert.Contains("ComputeCapacity(_capacity, count)", upload);
        Assert.DoesNotContain("new ", upload);
        Assert.Contains("_handle = GL.GenBuffer();", upload);
        Assert.Matches(new Regex(@"if \(_handle == 0\)\s*\{\s*_handle = GL\.GenBuffer\(\);"), upload);
    }

    [Fact]
    public void AnimatedModel_Uploads_Instances_Once_Per_Node_Not_Per_Primitive()
    {
        string source = Read("dream/Rendering/Models/AnimatedModel.cs");
        string method = Method(source, "private void RenderNodeInstanced(");

        int upload = method.IndexOf("_instanceMatrixBuffer.Upload(modelMatrices, count);", StringComparison.Ordinal);
        int loop = method.IndexOf("foreach (MeshBuffers primitive in primitives)", StringComparison.Ordinal);
        Assert.True(upload >= 0 && loop > upload, "The instance matrices must be uploaded before the primitive loop.");
        Assert.Single(Regex.Matches(source, @"_instanceMatrixBuffer\.Upload\("));
        Assert.Contains("_instanceMatrixBuffer.BindAttributes();", method[loop..]);
        Assert.DoesNotContain("UploadInstanceModelMatrices", source);
        Assert.DoesNotContain("BufferUsageHint.DynamicDraw", source);
        Assert.DoesNotContain("GL.GenBuffer()", Method(source, "private void RenderNodeInstanced("));
        Assert.Contains("_instanceMatrixBuffer.Dispose();", Method(source, "public void Dispose()"));
    }

    [Fact]
    public void WorldRenderer_Draws_Dynamic_Cubes_And_Blocks_Only_Through_Instanced_Batches()
    {
        string source = Read("dream/Rendering/World/WorldRenderer.cs");

        Assert.DoesNotContain("_render.DrawCube(", source);
        Assert.DoesNotContain("_render.DrawGeometryBlock(", source);
        Assert.Contains("_instanceDrawer.DrawCubeBatches(_render, camera, batches);", source);
        Assert.Contains("_instanceDrawer.DrawGeometryBlockBatches(_render, camera, dynamicBlockBatches);", source);
        Assert.Contains("_instanceDrawer.Dispose();", Method(source, "public void Dispose()"));
    }

    [Fact]
    public void Render_Fallback_Instances_Use_The_Opaque_Cube_Program_With_Instanced_Normals()
    {
        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        string fallback = Method(render, "public void DrawFallbackInstances(");
        string bind = Method(render, "private void BindTerrainMaterial(");

        Assert.Contains("BindTerrainMaterial(camera, Matrix4.Identity, material, instanced: true)", fallback);
        Assert.Contains("GL.CullFace(TriangleFace.Back);", fallback);
        Assert.Contains("instances.Upload(modelMatrices, count);", fallback);
        Assert.Contains("_fallbackCubeBuffer.DrawInstanced(instances, count);", fallback);
        Assert.Contains("GetShader(\"cube\").SetBool(\"useInstancing\", false);", fallback);
        Assert.Contains("shader.SetBool(\"useInstancing\", instanced);", bind);
        Assert.Matches(new Regex(@"if \(!instanced\)\s*\{\s*shader\.SetModelMatrix\(modelMatrix\);"), bind);

        string meshBuffer = Read("dream/Rendering/Core/StaticMeshBuffer.cs");
        string drawInstanced = Method(meshBuffer, "public void DrawInstanced(");
        Assert.Contains("instances.BindAttributes();", drawInstanced);
        Assert.Contains("GL.DrawElementsInstanced(", drawInstanced);
        Assert.Single(Regex.Matches(drawInstanced, @"RenderCounters\.CountDraw\(\);"));

        // The instance attribute is the shader's instanceModel input.
        string vertex = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", "cubeVertex.glsl"));
        Assert.Contains($"layout (location = {InstanceMatrixBuffer.FirstAttributeLocation}) in mat4 instanceModel;", vertex);
    }

    private static List<Cube> CreateCubes(int count, int materialCount)
    {
        List<Cube> cubes = new(count);
        for (int i = 0; i < count; i++)
        {
            cubes.Add(new Cube
            {
                MaterialId = Materials[i % materialCount],
                ShaderName = "cube",
                Position = new Vector3(i * 1.5f, (i % 7) * 0.25f, -(i % 11))
            });
        }

        return cubes;
    }

    private static List<GeometryRenderBlockDescriptor> CreateBlocks(int count, int materialCount)
    {
        List<GeometryRenderBlockDescriptor> blocks = new(count);
        for (int i = 0; i < count; i++)
        {
            blocks.Add(new GeometryRenderBlockDescriptor(
                new GeometryCellKey(i, 1, 1),
                Center: new Vector3(i, 0.5f, 2f),
                SideLength: 0.25f,
                Materials[i % materialCount],
                "cube",
                VisualFillScale: 0.9f));
        }

        return blocks;
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    // Stands in for Render: one submission per call, counted like the GL instanced draw.
    private sealed class RecordingTarget : IWorldInstanceDrawTarget
    {
        public int ModelCalls;
        public int FallbackCalls;
        public int TotalInstances;
        public bool CaptureMatrices;
        public readonly List<Matrix4> Captured = new();
        public InstanceMatrixBuffer? LastInstances;
        public Matrix4[]? LastMatrices;

        public void DrawModelInstances(Camera camera, AnimatedModel animation, string shaderName, Matrix4[] modelMatrices, int count)
        {
            ModelCalls++;
            Record(modelMatrices, count);
        }

        public void DrawFallbackInstances(
            Camera camera,
            GeometryMaterialId materialId,
            InstanceMatrixBuffer instances,
            Matrix4[] modelMatrices,
            int count)
        {
            FallbackCalls++;
            LastInstances = instances;
            Record(modelMatrices, count);
        }

        private void Record(Matrix4[] modelMatrices, int count)
        {
            RenderCounters.CountDraw();
            TotalInstances += count;
            LastMatrices = modelMatrices;
            if (CaptureMatrices)
            {
                for (int i = 0; i < count; i++)
                {
                    Captured.Add(modelMatrices[i]);
                }
            }
        }
    }
}
