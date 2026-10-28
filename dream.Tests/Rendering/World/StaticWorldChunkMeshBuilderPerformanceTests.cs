using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using System.Diagnostics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// The one mesh-builder test that asserts a wall-clock bound. It stays in the serial
// TimingSensitive collection so a busy parallel phase cannot push it over its budget; the rest of
// StaticWorldChunkMeshBuilderTests runs in parallel.
[Collection(TimingSensitiveCollection.Name)]
public class StaticWorldChunkMeshBuilderPerformanceTests
{
    [Fact]
    public void BuildDenseGridCompletesWithinInteractiveBudget()
    {
        const int side = 56;
        StaticVoxelRenderCell[] cells = Enumerable
            .Range(0, side)
            .SelectMany(x => Enumerable
                .Range(0, side)
                .Select(z => new StaticVoxelRenderCell(
                    new Vector3(x + 0.5f, 0.5f, z + 0.5f),
                    Vector3.One,
                    GeometryMaterialId.Grass)))
            .ToArray();

        Stopwatch stopwatch = Stopwatch.StartNew();
        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            StaticTerrainMaterialRole.Top,
            cells,
            cells);
        stopwatch.Stop();

        Assert.Equal(side * side, mesh.ExposedFaceCount);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 250,
            $"Dense grid mesh build took {stopwatch.ElapsedMilliseconds} ms.");
    }
}
