using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// A build that completes without a batch is treated as a fault; its dirty chunks must be
// retried like those of a build that threw.
public class StaticWorldChunkRendererNullBatchTests
{
    // A faulted or pending build never uploads or draws meshes, so Render is never touched.
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);

    [Fact]
    public void NullBatchBuild_DirtyChunksAreCarriedIntoTheRetriedRequest()
    {
        List<StaticWorldChunkBuildRequest> requests = [];
        TaskCompletionSource<StaticWorldChunkBuildBatch> pending = new();
        RenderFaultLog faults = new(_ => { });
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                requests.Add(request);
                return requests.Count == 1
                    ? Task.FromResult<StaticWorldChunkBuildBatch>(null!)
                    : pending.Task;
            },
            faults);

        StaticWorldChunkKey failedChunk = StaticWorldChunkKey.FromWorldPosition(Vector3.Zero);

        renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 1, staticDirtyCells: [new GeometryCellKey(0, 0, 0)]));
        Assert.Single(requests);
        Assert.Contains(failedChunk, requests[0].DirtyChunks);

        // This frame observes the null batch and starts the backoff.
        renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 1, staticDirtyCells: []));
        Assert.Equal(1, faults.GetFaultCount(StaticWorldChunkRenderer.BuildFaultSource));

        GeometryRenderExport quietExport = CreateExport(version: 2, staticDirtyCells: []);
        int frames = 0;
        while (requests.Count == 1 && frames <= StaticWorldChunkRenderer.BuildFaultMaxBackoffFrames)
        {
            renderer.Draw(UnusedRender, TestCamera, quietExport);
            frames++;
        }

        Assert.Equal(2, requests.Count);
        Assert.Contains(failedChunk, requests[1].DirtyChunks);
    }

    private static GeometryRenderExport CreateExport(long version, IReadOnlyList<GeometryCellKey> staticDirtyCells)
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            MaterialId = GeometryMaterialId.Grass,
            ShaderName = "default"
        };

        return new GeometryRenderExport(
            DirtyCells: staticDirtyCells,
            Cells: [],
            Blocks: [],
            CompatibilityCubes: [cube],
            staticDirtyCells: staticDirtyCells)
        {
            StaticContentVersion = version
        };
    }
}
