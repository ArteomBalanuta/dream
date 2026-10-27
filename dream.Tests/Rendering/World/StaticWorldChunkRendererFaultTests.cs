using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkRendererFaultTests
{
    // A faulted or pending build never uploads or draws meshes, so Render is never touched.
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);

    [Fact]
    public void FaultedBuild_IsReported_DoesNotThrow_AndReschedulesWithDirtyChunksAfterBackoff()
    {
        List<StaticWorldChunkBuildRequest> requests = [];
        TaskCompletionSource<StaticWorldChunkBuildBatch> pending = new();
        RenderFaultLog faults = new(_ => { });
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                requests.Add(request);
                return requests.Count == 1
                    ? Task.FromException<StaticWorldChunkBuildBatch>(new InvalidOperationException("bad chunk"))
                    : pending.Task;
            },
            faults);
        GeometryRenderExport firstExport = CreateExport(version: 1, staticDirtyCells: [new GeometryCellKey(0, 0, 0)]);
        GeometryRenderExport secondExport = CreateExport(version: 2, staticDirtyCells: []);

        renderer.Draw(UnusedRender, TestCamera, firstExport);
        Assert.Single(requests);

        StaticWorldChunkRenderStats stats = renderer.Draw(UnusedRender, TestCamera, secondExport);

        Assert.Equal(1, faults.GetFaultCount(StaticWorldChunkRenderer.BuildFaultSource));
        Assert.IsType<InvalidOperationException>(faults.LastFault);
        Assert.Equal(0, stats.RebuiltMeshes);
        Assert.Single(requests);

        int framesUntilRetry = 0;
        while (requests.Count == 1 && framesUntilRetry <= StaticWorldChunkRenderer.BuildFaultMaxBackoffFrames)
        {
            renderer.Draw(UnusedRender, TestCamera, secondExport);
            framesUntilRetry++;
        }

        Assert.Equal(2, requests.Count);
        Assert.Equal(StaticWorldChunkRenderer.CalculateBuildBackoffFrames(1), framesUntilRetry);
        Assert.Contains(new StaticWorldChunkKey(0, 0, 0), requests[1].DirtyChunks);
        Assert.Equal(StaticWorldChunkRenderer.CalculateExportSignature(secondExport), requests[1].ExportSignature);
    }

    [Fact]
    public void ThrowingBuildFactory_IsTreatedAsFaultedBuild()
    {
        int calls = 0;
        RenderFaultLog faults = new(_ => { });
        using StaticWorldChunkRenderer renderer = new(
            _ =>
            {
                calls++;
                throw new InvalidOperationException("factory failed");
            },
            faults);
        GeometryRenderExport export = CreateExport(version: 1, staticDirtyCells: []);

        renderer.Draw(UnusedRender, TestCamera, export);
        renderer.Draw(UnusedRender, TestCamera, export);

        Assert.Equal(1, calls);
        Assert.Equal(1, faults.FaultCount);
    }

    [Fact]
    public void Dispose_ObservesInFlightBuildAndReportsItsLateFault()
    {
        TaskCompletionSource<StaticWorldChunkBuildBatch> pending = new();
        RenderFaultLog faults = new(_ => { });
        StaticWorldChunkRenderer renderer = new(_ => pending.Task, faults);
        renderer.Draw(UnusedRender, TestCamera, CreateExport(version: 1, staticDirtyCells: []));

        renderer.Dispose();
        pending.SetException(new InvalidOperationException("late"));

        Assert.True(SpinWait.SpinUntil(() => faults.FaultCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, faults.GetFaultCount(StaticWorldChunkRenderer.BuildFaultSource));
    }

    [Fact]
    public void BuildBackoff_GrowsWithConsecutiveFaultsAndIsCapped()
    {
        Assert.Equal(0, StaticWorldChunkRenderer.CalculateBuildBackoffFrames(0));
        Assert.Equal(StaticWorldChunkRenderer.BuildFaultBaseBackoffFrames, StaticWorldChunkRenderer.CalculateBuildBackoffFrames(1));
        Assert.True(StaticWorldChunkRenderer.CalculateBuildBackoffFrames(2) > StaticWorldChunkRenderer.CalculateBuildBackoffFrames(1));
        Assert.Equal(StaticWorldChunkRenderer.BuildFaultMaxBackoffFrames, StaticWorldChunkRenderer.CalculateBuildBackoffFrames(100));
    }

    private static GeometryRenderExport CreateExport(long version, IReadOnlyList<GeometryCellKey> staticDirtyCells)
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            MaterialId = GeometryMaterialId.Grass,
            Animation = null!,
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
