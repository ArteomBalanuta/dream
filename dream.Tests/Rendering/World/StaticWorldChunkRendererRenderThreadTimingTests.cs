using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.World;

// GPU-1: reports the render-thread cost of the Draw that schedules a static chunk build on the
// startup world. The build task never completes, so nothing is uploaded and Render is untouched.
public class StaticWorldChunkRendererRenderThreadTimingTests(ITestOutputHelper output)
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);

    [Fact]
    public void SchedulingDraw_OnStartupWorld_ReportsRenderThreadTime()
    {
        Map map = new(
            physics: null,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(12f, 2f, 12f), 16f);

        // Warm the JIT on a first renderer so the measured Draw is steady-state.
        double best = double.MaxValue;
        double bestPlan = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            TaskCompletionSource<StaticWorldChunkBuildBatch> pending = new();
            int requests = 0;
            using StaticWorldChunkRenderer renderer = new(_ =>
            {
                requests++;
                return pending.Task;
            });

            long start = Stopwatch.GetTimestamp();
            StaticWorldChunkRenderStats stats = renderer.Draw(UnusedRender, TestCamera, export);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(1, requests);
            best = Math.Min(best, elapsed);
            bestPlan = Math.Min(bestPlan, stats.PlanMilliseconds);
        }

        // The plan itself now runs on the build worker; timed here only for the record.
        IReadOnlyList<StaticVoxelRenderCell> cells = StaticWorldChunkExtractor.ExtractCells(export);
        double bestWorkerPlan = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long start = Stopwatch.GetTimestamp();
            Assert.NotEmpty(StaticWorldChunkRenderer.PlanBuildInputs(cells));
            bestWorkerPlan = Math.Min(bestWorkerPlan, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        output.WriteLine(
            $"cells {cells.Count}: scheduling Draw {best:F3} ms, plan stage {bestPlan:F3} ms, " +
            $"worker plan {bestWorkerPlan:F3} ms");
    }
}
