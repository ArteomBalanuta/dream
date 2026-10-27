using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.World;

// The presentation export and its static signature run every client frame. On the startup world
// rebuilding them from scratch cost about 2 MB per frame; steady-state frames must stay cheap.
public class PresentationExportAllocationTests
{
    private const int WarmupFrames = 20;
    private const int MeasuredFrames = 200;
    private static readonly Vector3 CameraPosition = new(28f, 3.5f, 28f);

    private readonly ITestOutputHelper _output;

    public PresentationExportAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SteadyStateFrame_OnStartupWorld_AllocatesLittle()
    {
        Map map = new(
            physics: null,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
        AddDynamicCubes(map, count: 6);

        int signature = 0;
        for (int frame = 0; frame < WarmupFrames; frame++)
        {
            signature ^= CaptureFrame(map);
        }

        GeometryRenderExport sample = map.CapturePresentationGeometryRenderExport(CameraPosition, WorldVisibility.MaxRenderDistance);
        _output.WriteLine(
            $"world: {map.SolidCubes.Count} solid cubes, {sample.Cells.Count} cells, " +
            $"{sample.CompatibilityCubes.Count} static cubes, {sample.Blocks.Count} static blocks, " +
            $"{sample.DynamicCompatibilityCubes.Count} dynamic cubes");

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int frame = 0; frame < MeasuredFrames; frame++)
        {
            signature ^= CaptureFrame(map);
        }

        stopwatch.Stop();
        long bytesPerFrame = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / MeasuredFrames;
        double microsecondsPerFrame = stopwatch.Elapsed.TotalMilliseconds * 1000d / MeasuredFrames;
        _output.WriteLine($"steady-state frame: {bytesPerFrame} bytes, {microsecondsPerFrame:F1} us (signature {signature})");

        // Rebuilding the full export cost about 2 MB per frame on this world.
        Assert.True(bytesPerFrame < 16 * 1024, $"steady-state frame allocated {bytesPerFrame} bytes");
    }

    private static int CaptureFrame(Map map)
    {
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(
            CameraPosition,
            WorldVisibility.MaxRenderDistance);
        return StaticWorldChunkRenderer.CalculateExportSignature(export);
    }

    private static void AddDynamicCubes(Map map, int count)
    {
        int added = 0;
        for (int x = 20; x < map.Width && added < count; x++)
        {
            for (int z = 20; z < map.Depth && added < count; z++)
            {
                if (map.TryGetGeometryCellAtCell(x, 1, z) == null &&
                    map.TryAddCube(new Vector3(x, 1f, z), withPhysics: false) != null)
                {
                    added++;
                }
            }
        }

        Assert.Equal(count, added);
    }
}
