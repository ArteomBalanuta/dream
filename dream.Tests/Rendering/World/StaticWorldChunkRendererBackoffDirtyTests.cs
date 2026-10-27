using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkRendererBackoffDirtyTests
{
    // A faulted or pending build never uploads or draws meshes, so Render is never touched.
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);

    [Fact]
    public void DirtyChunksReportedDuringBuildBackoff_AreCarriedIntoTheRequestScheduledAfterIt()
    {
        List<StaticWorldChunkBuildRequest> requests = [];
        StaticWorldChunkRenderer renderer = CreateRendererThatFaultsFirstBuild(requests, out RenderFaultLog faults);
        using (renderer)
        {
            StaticWorldChunkKey faultedChunk = StaticWorldChunkKey.FromWorldPosition(Vector3.Zero);
            StaticWorldChunkKey backoffChunk = StaticWorldChunkKey.FromWorldPosition(new Vector3(100f, 0f, 0f));
            Assert.NotEqual(faultedChunk, backoffChunk);

            renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 1, staticDirtyCells: [new GeometryCellKey(0, 0, 0)]));
            Assert.Single(requests);

            // This frame observes the fault and starts the backoff.
            renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 1, staticDirtyCells: []));
            Assert.Equal(1, faults.GetFaultCount(StaticWorldChunkRenderer.BuildFaultSource));

            // The map drains its static dirty cells on every capture, so this cell is reported once only.
            renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 2, staticDirtyCells: [new GeometryCellKey(100, 0, 0)]));
            Assert.Single(requests);

            GeometryRenderExport quietExport = CreateExport(version: 3, staticDirtyCells: []);
            DrawUntilSecondRequest(renderer, requests, quietExport);

            Assert.Equal(2, requests.Count);
            Assert.Equal(StaticWorldChunkRenderer.CalculateExportSignature(quietExport), requests[1].ExportSignature);
            Assert.Contains(faultedChunk, requests[1].DirtyChunks);
            Assert.Contains(backoffChunk, requests[1].DirtyChunks);
        }
    }

    [Fact]
    public void DirtyChunksFromSeveralBackoffFrames_AreAllCarriedIntoTheNextRequest()
    {
        List<StaticWorldChunkBuildRequest> requests = [];
        StaticWorldChunkRenderer renderer = CreateRendererThatFaultsFirstBuild(requests, out _);
        using (renderer)
        {
            StaticWorldChunkKey firstChunk = StaticWorldChunkKey.FromWorldPosition(new Vector3(100f, 0f, 0f));
            StaticWorldChunkKey secondChunk = StaticWorldChunkKey.FromWorldPosition(new Vector3(-100f, 0f, 0f));

            renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 1, staticDirtyCells: []));
            renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 1, staticDirtyCells: []));
            renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 2, staticDirtyCells: [new GeometryCellKey(100, 0, 0)]));
            renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 3, staticDirtyCells: [new GeometryCellKey(-100, 0, 0)]));
            Assert.Single(requests);

            DrawUntilSecondRequest(renderer, requests, CreateExport(version: 4, staticDirtyCells: []));

            Assert.Equal(2, requests.Count);
            Assert.Contains(firstChunk, requests[1].DirtyChunks);
            Assert.Contains(secondChunk, requests[1].DirtyChunks);
        }
    }

    private static StaticWorldChunkRenderer CreateRendererThatFaultsFirstBuild(
        List<StaticWorldChunkBuildRequest> requests,
        out RenderFaultLog faults)
    {
        TaskCompletionSource<StaticWorldChunkBuildBatch> pending = new();
        faults = new RenderFaultLog(_ => { });
        return new StaticWorldChunkRenderer(
            request =>
            {
                requests.Add(request);
                return requests.Count == 1
                    ? Task.FromException<StaticWorldChunkBuildBatch>(new InvalidOperationException("bad chunk"))
                    : pending.Task;
            },
            faults);
    }

    private static void DrawUntilSecondRequest(
        StaticWorldChunkRenderer renderer,
        List<StaticWorldChunkBuildRequest> requests,
        GeometryRenderExport export)
    {
        int frames = 0;
        while (requests.Count == 1 && frames <= StaticWorldChunkRenderer.BuildFaultMaxBackoffFrames)
        {
            renderer.Draw(UnusedRender, TestCamera, export);
            frames++;
        }
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
