using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Integration;

// GFX-5/6 made a faulted static chunk build report, back off and retry; MEM-8 rewrote the mesh
// builder the worker runs. Here the worker runs the real default build (BuildBatch on a pool
// thread, through StaticWorldChunkMeshBuilder) on a real presentation export, and only the first
// attempt is made to fault inside the worker. Uploads need GL, so the test stops at the batch
// the retry produced, before the Draw that would upload it.
public class ChunkBuildFaultRecoveryIntegrationTests(ITestOutputHelper output)
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void FirstWorkerBuildFaults_RetryAfterBackoffRunsRealMeshBuilder_AndProducesTheExpectedChunks()
    {
        Map map = new(
            physics: null,
            loadVisualAssets: false,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(12f, 2f, 12f), 16f);
        Assert.NotEmpty(export.CompatibilityCubes);

        List<StaticWorldChunkBuildRequest> requests = [];
        List<Task<StaticWorldChunkBuildBatch>> tasks = [];
        List<string> logLines = [];
        RenderFaultLog faults = new(logLines.Add);
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                requests.Add(request);
                bool injectFault = requests.Count == 1;
                // The same work the default path does (Task.Run + BuildBatch), with the fault
                // thrown on the worker thread for the first attempt only.
                Task<StaticWorldChunkBuildBatch> task = Task.Run(() => injectFault
                    ? throw new InvalidOperationException("injected worker fault")
                    : StaticWorldChunkRenderer.BuildBatch(request));
                tasks.Add(task);
                return task;
            },
            faults);

        renderer.Draw(UnusedRender, TestCamera, export);
        Assert.Single(requests);
        WaitForCompletion(tasks[0]);
        Assert.True(tasks[0].IsFaulted);

        // The frame that observes the fault reports it and starts the backoff instead of throwing.
        StaticWorldChunkRenderStats faultFrame = renderer.Draw(UnusedRender, TestCamera, export);
        Assert.Equal(0, faultFrame.RebuiltMeshes);
        Assert.Equal(1, faults.GetFaultCount(StaticWorldChunkRenderer.BuildFaultSource));
        Assert.Equal("injected worker fault", Assert.IsType<InvalidOperationException>(faults.LastFault).Message);
        Assert.NotEmpty(logLines);
        Assert.Single(requests);

        int framesUntilRetry = 0;
        while (requests.Count == 1 && framesUntilRetry <= StaticWorldChunkRenderer.BuildFaultMaxBackoffFrames)
        {
            renderer.Draw(UnusedRender, TestCamera, export);
            framesUntilRetry++;
        }

        Assert.Equal(2, requests.Count);
        Assert.Equal(StaticWorldChunkRenderer.CalculateBuildBackoffFrames(1), framesUntilRetry);
        WaitForCompletion(tasks[1]);
        Assert.True(tasks[1].IsCompletedSuccessfully, tasks[1].Exception?.ToString());
        StaticWorldChunkBuildBatch retried = tasks[1].Result;

        // The retry covers the export the failed attempt was building, including its dirty chunks.
        StaticWorldChunkBuildRequest failed = requests[0];
        StaticWorldChunkBuildRequest retry = requests[1];
        Assert.Equal(StaticWorldChunkRenderer.CalculateExportSignature(export), retried.ExportSignature);
        Assert.True(failed.DirtyChunks.IsSubsetOf(retry.DirtyChunks));

        // Expected chunks: what a synchronous build of a fresh plan of the same export yields.
        IReadOnlyList<StaticWorldChunkBuildInput> expectedInputs = StaticWorldChunkRenderer.PlanBuildInputs(
            StaticWorldChunkExtractor.ExtractCells(export));
        StaticWorldChunkBuildOutput[] expected = StaticWorldChunkRenderer.BuildOutputs(expectedInputs)
            .OrderBy(o => o.Key.ToString(), StringComparer.Ordinal).ToArray();
        StaticWorldChunkBuildOutput[] actual = retried.Outputs
            .OrderBy(o => o.Key.ToString(), StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(o => o.Key), actual.Select(o => o.Key));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Signature, actual[i].Signature);
            Assert.Equal(expected[i].Mesh.ExposedFaceCount, actual[i].Mesh.ExposedFaceCount);
            Assert.Equal(expected[i].Mesh.SourceCellCount, actual[i].Mesh.SourceCellCount);
            Assert.Equal(expected[i].Mesh.Bounds, actual[i].Mesh.Bounds);
            Assert.True(expected[i].Mesh.Vertices.SequenceEqual(actual[i].Mesh.Vertices), $"vertices differ for {expected[i].Key}");
            Assert.True(expected[i].Mesh.Indices.SequenceEqual(actual[i].Mesh.Indices), $"indices differ for {expected[i].Key}");
        }

        Assert.Contains(actual, o => o.Mesh.ExposedFaceCount > 0 && o.Mesh.TriangleCount > 0);
        // The failed attempt's inputs, built now, give the same meshes: nothing was lost to the fault.
        StaticWorldChunkBuildOutput[] failedRebuilt = StaticWorldChunkRenderer.BuildBatch(failed).Outputs
            .OrderBy(o => o.Key.ToString(), StringComparer.Ordinal).ToArray();
        Assert.Equal(actual.Select(o => (o.Key, o.Signature)), failedRebuilt.Select(o => (o.Key, o.Signature)));

        output.WriteLine(
            $"retry after {framesUntilRetry} frames: {actual.Length} meshes, " +
            $"{actual.Sum(o => o.Mesh.TriangleCount)} triangles, dirty chunks {retry.DirtyChunks.Count}");
    }

    private static void WaitForCompletion(Task task)
    {
        Assert.True(
            ((IAsyncResult)task).AsyncWaitHandle.WaitOne(BuildTimeout),
            "Chunk build task did not finish in time.");
    }
}
