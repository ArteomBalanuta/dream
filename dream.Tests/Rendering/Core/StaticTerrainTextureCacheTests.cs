using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class StaticTerrainTextureCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly StaticTerrainMaterial GrassTop = new(
        GeometryMaterialId.Grass,
        StaticTerrainMaterialRole.Top,
        Vector4.One,
        "grass-top.png");

    [Fact]
    public void GetOrLoad_WithoutTexture_ReturnsFallbackWithoutDecoding()
    {
        FakeBackend backend = new();
        using StaticTerrainTextureCache cache = backend.CreateCache();

        int texture = cache.GetOrLoad(GrassTop with { TexturePath = "" });

        Assert.Equal(-1, texture);
        Assert.Equal(0, backend.DecodeCount);
        Assert.Equal(0, cache.PendingCount);
    }

    [Fact]
    public void GetOrLoad_WhilePending_ReturnsFallbackAndDecodesOnce()
    {
        FakeBackend backend = new();
        using StaticTerrainTextureCache cache = backend.CreateCache();

        Assert.Equal(-1, cache.GetOrLoad(GrassTop));
        Assert.Equal(-1, cache.GetOrLoad(GrassTop));
        Assert.Equal(-1, cache.GetOrLoad(GrassTop));

        Assert.Equal(1, cache.PendingCount);
        Assert.Equal(0, backend.UploadCount);
        backend.ReleaseDecode();
        WaitForTexture(cache, GrassTop);
        Assert.Equal(1, backend.DecodeCount);
    }

    [Fact]
    public void GetOrLoad_AfterDecode_UploadsOnceOnCallingThread()
    {
        FakeBackend backend = new();
        using StaticTerrainTextureCache cache = backend.CreateCache();
        cache.GetOrLoad(GrassTop);
        backend.ReleaseDecode();

        int texture = WaitForTexture(cache, GrassTop);

        Assert.Equal(FakeBackend.FirstHandle, texture);
        Assert.Equal(texture, cache.GetOrLoad(GrassTop));
        Assert.Equal(texture, cache.GetOrLoad(GrassTop));
        Assert.Equal(1, backend.UploadCount);
        Assert.Equal(Environment.CurrentManagedThreadId, backend.UploadThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, backend.DecodeThreadId);
        Assert.Equal(0, cache.PendingCount);
    }

    [Fact]
    public void GetOrLoad_MissingFile_CachesFallbackWithoutRetryOrFault()
    {
        FakeBackend backend = new() { DecodeResult = null };
        using StaticTerrainTextureCache cache = backend.CreateCache();
        cache.GetOrLoad(GrassTop);
        backend.ReleaseDecode();

        Assert.True(SpinWait.SpinUntil(
            () => cache.GetOrLoad(GrassTop) == -1 && cache.PendingCount == 0,
            Timeout));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(-1, cache.GetOrLoad(GrassTop));
        }

        Assert.Equal(1, backend.DecodeCount);
        Assert.Equal(0, backend.UploadCount);
        Assert.Empty(backend.Faults);
    }

    [Fact]
    public void GetOrLoad_DecodeFault_ReportsOnceAndCachesFallback()
    {
        FakeBackend backend = new() { DecodeFault = new InvalidDataException("bad png") };
        using StaticTerrainTextureCache cache = backend.CreateCache();
        cache.GetOrLoad(GrassTop);
        backend.ReleaseDecode();

        Assert.True(SpinWait.SpinUntil(
            () => cache.GetOrLoad(GrassTop) == -1 && backend.Faults.Count > 0,
            Timeout));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(-1, cache.GetOrLoad(GrassTop));
        }

        (string source, Exception fault) = Assert.Single(backend.Faults);
        Assert.Equal(StaticTerrainTextureCache.FaultSource, source);
        Assert.IsType<InvalidDataException>(fault);
        Assert.Equal(1, backend.DecodeCount);
        Assert.Equal(0, backend.UploadCount);
        Assert.Equal(0, cache.PendingCount);
    }

    [Fact]
    public void GetOrLoad_UploadFault_ReportsOnceAndCachesFallback()
    {
        FakeBackend backend = new() { UploadFault = new InvalidOperationException("no context") };
        using StaticTerrainTextureCache cache = backend.CreateCache();
        cache.GetOrLoad(GrassTop);
        backend.ReleaseDecode();

        Assert.True(SpinWait.SpinUntil(
            () => cache.GetOrLoad(GrassTop) == -1 && backend.Faults.Count > 0,
            Timeout));
        Assert.Equal(-1, cache.GetOrLoad(GrassTop));

        Assert.Single(backend.Faults);
        Assert.Equal(1, backend.UploadCount);
        Assert.Equal(1, backend.DecodeCount);
    }

    [Fact]
    public void GetOrLoad_DistinctRoles_LoadIndependently()
    {
        FakeBackend backend = new();
        using StaticTerrainTextureCache cache = backend.CreateCache();
        StaticTerrainMaterial grassSide = GrassTop with { Role = StaticTerrainMaterialRole.Side };
        cache.GetOrLoad(GrassTop);
        cache.GetOrLoad(grassSide);
        backend.ReleaseDecode();

        int top = WaitForTexture(cache, GrassTop);
        int side = WaitForTexture(cache, grassSide);

        Assert.NotEqual(top, side);
        Assert.Equal(2, backend.DecodeCount);
        Assert.Equal(2, backend.UploadCount);
    }

    [Fact]
    public void Dispose_DeletesUploadedTexturesOnceAndIsIdempotent()
    {
        FakeBackend backend = new();
        StaticTerrainTextureCache cache = backend.CreateCache();
        cache.GetOrLoad(GrassTop);
        backend.ReleaseDecode();
        int texture = WaitForTexture(cache, GrassTop);

        cache.Dispose();
        cache.Dispose();

        Assert.Equal([texture], backend.DeletedTextures);
    }

    [Fact]
    public void Dispose_ObservesPendingDecodeFaultAndStopsLoading()
    {
        FakeBackend backend = new() { DecodeFault = new InvalidDataException("late failure") };
        StaticTerrainTextureCache cache = backend.CreateCache();
        cache.GetOrLoad(GrassTop);

        cache.Dispose();
        backend.ReleaseDecode();

        Assert.True(SpinWait.SpinUntil(() => backend.Faults.Count > 0, Timeout));
        (string source, Exception fault) = Assert.Single(backend.Faults);
        Assert.Equal(StaticTerrainTextureCache.FaultSource, source);
        Assert.IsType<InvalidDataException>(fault);
        Assert.Equal(-1, cache.GetOrLoad(GrassTop));
        Assert.Equal(1, backend.DecodeCount);
        Assert.Equal(0, backend.UploadCount);
        Assert.Empty(backend.DeletedTextures);
    }

    private static int WaitForTexture(StaticTerrainTextureCache cache, StaticTerrainMaterial material)
    {
        int texture = -1;
        Assert.True(SpinWait.SpinUntil(
            () => (texture = cache.GetOrLoad(material)) != -1,
            Timeout));
        return texture;
    }

    private sealed class FakeBackend : IRenderFaultSink
    {
        public const int FirstHandle = 100;

        private readonly ManualResetEventSlim _decodeGate = new(false);
        private readonly object _sync = new();
        private readonly List<(string Source, Exception Fault)> _faults = [];
        private int _decodeCount;
        private int _nextHandle = FirstHandle;

        public DecodedTexture? DecodeResult { get; init; } = new(1, 1, [1, 2, 3, 4]);

        public Exception? DecodeFault { get; init; }

        public Exception? UploadFault { get; init; }

        public int DecodeCount => Volatile.Read(ref _decodeCount);

        public int DecodeThreadId { get; private set; }

        public int UploadCount { get; private set; }

        public int UploadThreadId { get; private set; }

        public List<int> DeletedTextures { get; } = [];

        public IReadOnlyList<(string Source, Exception Fault)> Faults
        {
            get
            {
                lock (_sync)
                {
                    return _faults.ToArray();
                }
            }
        }

        public StaticTerrainTextureCache CreateCache() => new(Decode, Upload, DeletedTextures.Add, this);

        public void ReleaseDecode() => _decodeGate.Set();

        public void Report(string source, Exception exception)
        {
            lock (_sync)
            {
                _faults.Add((source, exception));
            }
        }

        private DecodedTexture? Decode(string path)
        {
            Interlocked.Increment(ref _decodeCount);
            DecodeThreadId = Environment.CurrentManagedThreadId;
            Assert.True(_decodeGate.Wait(Timeout));
            if (DecodeFault != null)
            {
                throw DecodeFault;
            }

            return DecodeResult;
        }

        private int Upload(DecodedTexture texture)
        {
            UploadCount++;
            UploadThreadId = Environment.CurrentManagedThreadId;
            if (UploadFault != null)
            {
                throw UploadFault;
            }

            return _nextHandle++;
        }
    }
}
