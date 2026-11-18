using System.Collections.Concurrent;
using System.Diagnostics;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class StaticTerrainTileArrayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Layer_Paths_Follow_Geometry_Material_Order_With_The_Grass_Top_Tile()
    {
        IReadOnlyList<string> paths = StaticTerrainTileArray.LayerPaths;

        Assert.Equal(8, paths.Count);
        string[] expected = ["grass_top", "dirt", "stone", "sand", "mud", "snow", "rock", "grass_path"];
        for (int layer = 0; layer < expected.Length; layer++)
        {
            Assert.EndsWith(Path.Combine(expected[layer], expected[layer] + ".albedo.png"), paths[layer]);
            GeometryMaterialId id = (GeometryMaterialId)layer;
            StaticTerrainMaterialRole role = id == GeometryMaterialId.Grass ? StaticTerrainMaterialRole.Top : StaticTerrainMaterialRole.Single;
            Assert.Equal(StaticTerrainMaterialCatalog.Resolve(id, role).TexturePath, paths[layer]);
        }
    }

    [Fact]
    public void GetOrLoad_Returns_Minus_One_Until_All_Eight_Layers_Are_Uploaded()
    {
        FakeBackend backend = new();
        using StaticTerrainTileArray array = backend.Create();

        Assert.Equal(-1, array.GetOrLoad());
        Assert.False(array.IsReady);
        Assert.Equal(0, backend.AllocateCount);

        int handle = WaitReady(array, backend);

        Assert.Equal(FakeBackend.Handle, handle);
        Assert.Equal(1, backend.AllocateCount);
        Assert.Equal(Enumerable.Range(0, 8), backend.UploadedLayers.Order());
        Assert.Equal(8, backend.DecodedPaths.Distinct().Count());
        Assert.Equal(handle, array.GetOrLoad());
        Assert.Equal(8, backend.UploadedLayers.Count);
        Assert.True(array.IsReady);
    }

    [Fact]
    public void GetOrLoad_Uploads_At_Most_One_Layer_Per_Call()
    {
        FakeBackend backend = new();
        using StaticTerrainTileArray array = backend.Create();
        array.GetOrLoad();

        // DecodeCount is incremented when a decode STARTS, and a fixed 12 calls after a 50 ms
        // sleep was not enough for the eight decode tasks to finish on a starved thread pool: it
        // read 7 uploaded of 8 under load. The loop therefore runs until every layer is uploaded,
        // within the same generous bound as the rest of the class, and keeps checking the
        // one-layer-per-call rule on every call - more calls means more checks, not a weaker test.
        int previous = 0;
        int calls = 0;
        Stopwatch elapsed = Stopwatch.StartNew();
        while (previous < 8)
        {
            Assert.True(elapsed.Elapsed < Timeout, $"only {previous} of 8 layers were uploaded in {calls} calls");
            array.GetOrLoad();
            calls++;
            int now = backend.UploadedLayers.Count;
            Assert.InRange(now - previous, 0, 1);
            if (now == previous)
            {
                Thread.Yield();
            }

            previous = now;
        }

        Assert.Equal(8, previous);

        // One layer per call means the eight layers cannot have been uploaded in fewer than eight.
        Assert.True(calls >= 8, $"8 layers were uploaded in only {calls} calls");

        // And no later call uploads a ninth.
        array.GetOrLoad();
        Assert.Equal(8, backend.UploadedLayers.Count);
    }

    [Fact]
    public void A_Non_256_Tile_Caches_Minus_One_For_Good_And_Frees_The_Array()
    {
        FakeBackend backend = new() { BadLayerSize = true };
        using StaticTerrainTileArray array = backend.Create();

        Assert.True(SpinWait.SpinUntil(() => array.GetOrLoad() == -1 && array.HasFailed, Timeout));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(-1, array.GetOrLoad());
        }

        Assert.Single(backend.Faults);
        Assert.Equal(StaticTerrainTileArray.FaultSource, backend.Faults[0]);
        Assert.False(array.IsReady);
        Assert.Equal(backend.AllocateCount, backend.Deleted.Count);

        // All eight decodes are queued by the first GetOrLoad, and the bad layer can be observed
        // while some of the other seven have not run yet (it read 7 of 8 under load). The decode
        // count is therefore waited for, not sampled - and then asserted exactly, so a ninth
        // decode or a decode the failure skipped still fails the test.
        Assert.True(
            SpinWait.SpinUntil(() => backend.DecodeCount >= 8, Timeout),
            $"only {backend.DecodeCount} of 8 layers were decoded");
        Assert.Equal(8, backend.DecodeCount);
    }

    [Fact]
    public void A_Missing_File_Caches_Minus_One_For_Good()
    {
        FakeBackend backend = new() { MissingLayer = true };
        using StaticTerrainTileArray array = backend.Create();

        Assert.True(SpinWait.SpinUntil(() => array.GetOrLoad() == -1 && array.HasFailed, Timeout));
        Assert.Equal(-1, array.GetOrLoad());
        Assert.Single(backend.Faults);
    }

    [Fact]
    public void A_Decode_Fault_Reports_Once_And_Caches_Minus_One()
    {
        FakeBackend backend = new() { DecodeFault = new InvalidDataException("bad png") };
        using StaticTerrainTileArray array = backend.Create();

        Assert.True(SpinWait.SpinUntil(() => array.GetOrLoad() == -1 && array.HasFailed, Timeout));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(-1, array.GetOrLoad());
        }

        // The first fault fails the array; the other seven faulted decodes are observed, not dropped.
        Assert.NotEmpty(backend.Faults);
        Assert.All(backend.Faults, source => Assert.Equal(StaticTerrainTileArray.FaultSource, source));
        Assert.Empty(backend.UploadedLayers);
    }

    [Fact]
    public void An_Upload_Fault_Reports_Deletes_The_Array_And_Caches_Minus_One()
    {
        FakeBackend backend = new() { UploadFault = new InvalidOperationException("no context") };
        using StaticTerrainTileArray array = backend.Create();

        Assert.True(SpinWait.SpinUntil(() => array.GetOrLoad() == -1 && array.HasFailed, Timeout));
        Assert.Equal(-1, array.GetOrLoad());

        Assert.Single(backend.Faults);
        Assert.Equal([FakeBackend.Handle], backend.Deleted);
        Assert.Equal(1, backend.AllocateCount);
    }

    [Fact]
    public void Dispose_Deletes_The_Array_Once_And_Later_Calls_Return_Minus_One()
    {
        FakeBackend backend = new();
        StaticTerrainTileArray array = backend.Create();
        int handle = WaitReady(array, backend);

        array.Dispose();
        array.Dispose();

        Assert.Equal([handle], backend.Deleted);
        Assert.Equal(-1, array.GetOrLoad());
    }

    [Fact]
    public void Dispose_Before_Ready_Deletes_What_Was_Allocated_And_Never_Uploads_Again()
    {
        FakeBackend backend = new();
        StaticTerrainTileArray array = backend.Create();
        // The same wait as GetOrLoad_Uploads_At_Most_One_Layer_Per_Call: a started decode is not a
        // finished one, so the array is driven until it has really allocated and uploaded a layer.
        // Stopping at the first upload keeps it short of ready, which is what this test is about.
        array.GetOrLoad();
        Assert.True(
            SpinWait.SpinUntil(
                () =>
                {
                    array.GetOrLoad();
                    return backend.UploadedLayers.Count > 0;
                },
                Timeout),
            "no layer was uploaded");

        array.Dispose();
        int uploaded = backend.UploadedLayers.Count;
        Assert.InRange(uploaded, 1, 7);

        Assert.Equal(-1, array.GetOrLoad());
        Assert.Equal(uploaded, backend.UploadedLayers.Count);
        Assert.Equal([FakeBackend.Handle], backend.Deleted);
    }

    private static int WaitReady(StaticTerrainTileArray array, FakeBackend backend)
    {
        int handle = -1;
        Assert.True(SpinWait.SpinUntil(() => (handle = array.GetOrLoad()) != -1, Timeout), $"faults: {backend.Faults.Count}");
        return handle;
    }

    private sealed class FakeBackend : IRenderFaultSink
    {
        public const int Handle = 700;

        private readonly ConcurrentQueue<string> _decodedPaths = new();
        private readonly ConcurrentQueue<string> _faults = new();
        private int _decodeCount;

        public bool BadLayerSize { get; init; }

        public bool MissingLayer { get; init; }

        public Exception? DecodeFault { get; init; }

        public Exception? UploadFault { get; init; }

        public int DecodeCount => Volatile.Read(ref _decodeCount);

        public int AllocateCount { get; private set; }

        public List<int> UploadedLayers { get; } = [];

        public List<int> Deleted { get; } = [];

        public List<string> DecodedPaths => [.. _decodedPaths];

        public List<string> Faults => [.. _faults];

        public StaticTerrainTileArray Create() => new(Decode, Allocate, Upload, Deleted.Add, this);

        public void Report(string source, Exception exception) => _faults.Enqueue(source);

        private DecodedTexture? Decode(string path)
        {
            _decodedPaths.Enqueue(path);
            Interlocked.Increment(ref _decodeCount);
            if (DecodeFault != null)
            {
                throw DecodeFault;
            }

            bool last = path == StaticTerrainTileArray.LayerPaths[7];
            if (MissingLayer && last)
            {
                return null;
            }

            int size = BadLayerSize && last ? 128 : StaticTerrainTileArray.TileSize;
            return new DecodedTexture(size, size, new byte[4 * size * size]);
        }

        private int Allocate()
        {
            AllocateCount++;
            return Handle;
        }

        private void Upload(int handle, int layer, DecodedTexture tile)
        {
            Assert.Equal(Handle, handle);
            if (UploadFault != null)
            {
                throw UploadFault;
            }

            UploadedLayers.Add(layer);
        }
    }
}
