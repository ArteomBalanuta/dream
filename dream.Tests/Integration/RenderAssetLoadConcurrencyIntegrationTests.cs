using System.Collections.Concurrent;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Integration;

// GFX-7 split texture and glTF decoding (thread pool) from GL upload (render thread); GFX-3 made
// Render own and dispose every GL resource. These run the decode/upload split under load: render
// threads hammer StaticTerrainTextureCache through its public test seam while decodes finish on
// the pool in arbitrary order, dispose mid-flight and keep calling afterwards; and eight threads
// load the same glTF asset at once.
public class RenderAssetLoadConcurrencyIntegrationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly GeometryMaterialId[] Materials =
        [GeometryMaterialId.Grass, GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Sand];

    private static readonly StaticTerrainMaterialRole[] Roles =
        [StaticTerrainMaterialRole.Top, StaticTerrainMaterialRole.Side, StaticTerrainMaterialRole.Bottom];

    [Fact]
    public void TextureCaches_OnSeveralRenderThreads_DisposedMidFlight_UploadEachKeyOnceAndDeleteEachUploadOnce()
    {
        const int RenderThreads = 3;
        StaticTerrainMaterial[] keys = Materials
            .SelectMany(material => Roles.Select(role => new StaticTerrainMaterial(
                material,
                role,
                Vector4.One,
                $"{material}-{role}.png")))
            .ToArray();

        CacheRun[] runs = Enumerable.Range(0, RenderThreads).Select(index => new CacheRun(index, keys)).ToArray();
        // Each render thread is a dedicated thread, like the game's; decodes run on the pool.
        Thread[] threads = runs.Select(run => new Thread(run.Execute) { IsBackground = true, Name = $"render-{run.Index}" }).ToArray();
        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Timeout), $"{thread.Name} did not finish.");
        }

        foreach (CacheRun run in runs)
        {
            Assert.Null(run.Escaped);
            run.AssertInvariants();
            output.WriteLine(run.Describe());
        }

        // Disposal really happened mid-flight somewhere: some decode finished after its cache was gone.
        Assert.True(runs.Sum(run => run.DecodesFinishedAfterDispose) > 0, "Every decode finished before Dispose.");
        Assert.True(runs.Sum(run => run.Uploads.Count) > 0, "Nothing was uploaded before Dispose.");
    }

    [Theory]
    [InlineData(nameof(WolfAssetDefinition))]
    [InlineData(nameof(PlayerModelAssetDefinition))]
    public void AnimatedModelData_LoadedFromEightThreadsAtOnce_IsIndependentAndIdentical(string asset)
    {
        string path = asset == nameof(WolfAssetDefinition) ? WolfAssetDefinition.ModelPath : PlayerModelAssetDefinition.ModelPath;
        const int ThreadCount = 8;
        AnimatedModelData?[] loaded = new AnimatedModelData?[ThreadCount];
        Exception?[] faults = new Exception?[ThreadCount];
        using Barrier start = new(ThreadCount);
        Thread[] threads = Enumerable.Range(0, ThreadCount)
            .Select(index => new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    loaded[index] = AnimatedModelData.Load(path);
                }
                catch (Exception ex)
                {
                    faults[index] = ex;
                }
            }) { IsBackground = true })
            .ToArray();
        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Timeout));
        }

        Assert.All(faults, fault => Assert.Null(fault));
        AnimatedModelData[] models = loaded.Select(model => Assert.IsType<AnimatedModelData>(model)).ToArray();
        string reference = Describe(models[0]);
        Assert.NotEmpty(models[0].MeshPrimitives.SelectMany(primitives => primitives));
        for (int i = 1; i < models.Length; i++)
        {
            Assert.Equal(reference, Describe(models[i]));
            AssertSameContentNoSharedState(models[0], models[i]);
        }

        // Independent: scribbling over one instance's buffers leaves every other one intact.
        AnimatedModelPrimitiveData scribbled = models[0].MeshPrimitives.SelectMany(primitives => primitives).First();
        scribbled.Vertices[0].Position = new Vector3(float.NaN);
        scribbled.Indices[0] = uint.MaxValue;
        foreach (DecodedTexture texture in models[0].Materials.SelectMany(Textures))
        {
            Array.Fill(texture.Rgba, (byte)0x5A);
        }

        for (int i = 1; i < models.Length; i++)
        {
            Assert.Equal(reference, Describe(models[i]));
        }

        output.WriteLine($"{asset}: {reference.Length} chars of content per instance, {models[0].Materials.Count} materials");
    }

    private static void AssertSameContentNoSharedState(AnimatedModelData left, AnimatedModelData right)
    {
        Assert.NotSame(left.Model, right.Model);
        Assert.NotSame(left.Pose, right.Pose);
        Assert.Equal(left.MeshPrimitives.Count, right.MeshPrimitives.Count);
        for (int mesh = 0; mesh < left.MeshPrimitives.Count; mesh++)
        {
            for (int p = 0; p < left.MeshPrimitives[mesh].Count; p++)
            {
                AnimatedModelPrimitiveData a = left.MeshPrimitives[mesh][p];
                AnimatedModelPrimitiveData b = right.MeshPrimitives[mesh][p];
                Assert.NotSame(a.Vertices, b.Vertices);
                Assert.NotSame(a.Indices, b.Indices);
                Assert.Equal(a.Vertices, b.Vertices);
                Assert.Equal(a.Indices, b.Indices);
            }
        }

        for (int m = 0; m < left.Materials.Count; m++)
        {
            Assert.NotSame(left.Materials[m], right.Materials[m]);
            DecodedTexture[] a = Textures(left.Materials[m]).ToArray();
            DecodedTexture[] b = Textures(right.Materials[m]).ToArray();
            Assert.Equal(a.Length, b.Length);
            for (int t = 0; t < a.Length; t++)
            {
                Assert.NotSame(a[t].Rgba, b[t].Rgba);
                Assert.Equal(a[t].Rgba, b[t].Rgba);
            }
        }
    }

    // Every value the GL upload consumes, as text, so instances compare in one assertion.
    private static string Describe(AnimatedModelData data)
    {
        System.Text.StringBuilder text = new();
        text.Append(data.SourcePath).Append('|').Append(data.Pose.NodeCount).Append('|');
        foreach (IReadOnlyList<AnimatedModelPrimitiveData> mesh in data.MeshPrimitives)
        {
            foreach (AnimatedModelPrimitiveData primitive in mesh)
            {
                text.Append("prim ").Append(primitive.MaterialIndex).Append(' ').Append(primitive.PrimitiveType)
                    .Append(' ').Append(primitive.UsesSkinning).Append(' ');
                foreach (AnimatedModelData.Vertex vertex in primitive.Vertices)
                {
                    text.Append(vertex.Position).Append(vertex.Normal).Append(vertex.Tangent).Append(vertex.TexCoord0)
                        .Append(vertex.Joints).Append(vertex.Weights);
                }

                text.Append(string.Join(',', primitive.Indices)).Append('\n');
            }
        }

        foreach (AnimatedModelMaterialData material in data.Materials)
        {
            text.Append("mat ").Append(material.BaseColorFactor).Append(material.EmissiveFactor).Append(material.MetallicFactor)
                .Append(material.RoughnessFactor).Append(material.AlphaCutoff).Append(material.AlphaMode);
            foreach (DecodedTexture texture in Textures(material))
            {
                text.Append(" tex ").Append(texture.Width).Append('x').Append(texture.Height).Append(':')
                    .Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(texture.Rgba)));
            }

            text.Append('\n');
        }

        foreach (AnimatedModelSkinJoint joint in data.SkinJoints ?? [])
        {
            text.Append("joint ").Append(joint.NodeIndex).Append(joint.InverseBindMatrix).Append('\n');
        }

        text.Append("anims ").Append(string.Join(',', data.Animations.Keys.Order(StringComparer.Ordinal)));
        return text.ToString();
    }

    private static IEnumerable<DecodedTexture> Textures(AnimatedModelMaterialData material)
    {
        return new[]
            {
                material.BaseColorTexture,
                material.NormalTexture,
                material.MetallicRoughnessTexture,
                material.EmissiveTexture,
                material.OcclusionTexture
            }
            .Where(texture => texture != null)
            .Select(texture => texture!);
    }

    // One render thread's cache. Decodes take a key-dependent time and some keys fail to decode,
    // so uploads, faults and still-pending decodes are interleaved when Dispose runs.
    private sealed class CacheRun : IRenderFaultSink
    {
        private readonly StaticTerrainMaterial[] _keys;
        private readonly ConcurrentDictionary<string, int> _decodes = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(string Source, Exception Fault)> _faults = new();
        private readonly ManualResetEventSlim _slowDecodes = new(false);
        private int _renderThreadId;
        private int _nextHandle;
        private volatile bool _disposed;
        private int _decodesFinished;
        private int _decodesFinishedAfterDispose;

        public CacheRun(int index, StaticTerrainMaterial[] keys)
        {
            Index = index;
            _keys = keys;
            _nextHandle = 1000 * (index + 1);
        }

        public int Index { get; }

        public Exception? Escaped { get; private set; }

        public List<(string Path, int Handle)> Uploads { get; } = [];

        public List<int> Deleted { get; } = [];

        public int UploadsAfterDispose { get; private set; }

        public int DecodesFinishedAfterDispose => Volatile.Read(ref _decodesFinishedAfterDispose);

        public Dictionary<string, int> LastHandles { get; } = new(StringComparer.Ordinal);

        public void Report(string source, Exception exception)
        {
            _faults.Enqueue((source, exception));
        }

        public void Execute()
        {
            _renderThreadId = Environment.CurrentManagedThreadId;
            StaticTerrainTextureCache cache = new(Decode, Upload, Deleted.Add, this);
            try
            {
                // The first frame starts every decode. Frames continue until every fast key is
                // resolved (uploaded or failed) while the slow keys are still decoding.
                DateTime deadline = DateTime.UtcNow + Timeout;
                do
                {
                    foreach (StaticTerrainMaterial key in _keys)
                    {
                        int handle = cache.GetOrLoad(key);
                        if (LastHandles.TryGetValue(key.TexturePath, out int previous) && previous != -1)
                        {
                            Assert.Equal(previous, handle);
                        }

                        LastHandles[key.TexturePath] = handle;
                    }

                    Thread.Yield();
                }
                while (cache.PendingCount > _keys.Count(key => IsSlowKey(key.TexturePath)) && DateTime.UtcNow < deadline);

                Assert.Equal(_keys.Count(key => IsSlowKey(key.TexturePath)), cache.PendingCount);
                _disposed = true;
                cache.Dispose();
                cache.Dispose();

                // The slow decodes finish after the cache is gone; frames keep calling it.
                _slowDecodes.Set();
                deadline = DateTime.UtcNow + Timeout;
                while (Volatile.Read(ref _decodesFinished) < _keys.Length && DateTime.UtcNow < deadline)
                {
                    foreach (StaticTerrainMaterial key in _keys)
                    {
                        Assert.Equal(-1, cache.GetOrLoad(key));
                    }

                    Thread.Yield();
                }

                Assert.Equal(_keys.Length, Volatile.Read(ref _decodesFinished));
                for (int frame = 0; frame < 50; frame++)
                {
                    foreach (StaticTerrainMaterial key in _keys)
                    {
                        Assert.Equal(-1, cache.GetOrLoad(key));
                    }
                }

                // A fault from a decode that was still pending at Dispose is observed off-thread.
                int failing = _keys.Count(key => IsFailingKey(key.TexturePath));
                SpinWait.SpinUntil(() => _faults.Count >= failing, Timeout);
            }
            catch (Exception ex)
            {
                Escaped = ex;
            }
        }

        public void AssertInvariants()
        {
            // Each key decoded exactly once and uploaded at most once, and only before Dispose.
            Assert.Equal(_keys.Length, _decodes.Count);
            Assert.All(_decodes, decode => Assert.Equal(1, decode.Value));
            Assert.Equal(0, UploadsAfterDispose);
            Assert.Equal(Uploads.Count, Uploads.Select(upload => upload.Path).Distinct(StringComparer.Ordinal).Count());

            // Every fast key that decodes cleanly was uploaded; no slow or failing key was.
            Assert.Equal(
                _keys.Select(key => key.TexturePath).Where(path => !IsSlowKey(path) && !IsFailingKey(path)).Order(StringComparer.Ordinal),
                Uploads.Select(upload => upload.Path).Order(StringComparer.Ordinal));

            // Every uploaded texture was deleted exactly once, and nothing else was deleted.
            Assert.Equal(Uploads.Select(upload => upload.Handle).Order(), Deleted.Order());

            // The handle frames saw is the one uploaded for that key.
            foreach ((string path, int handle) in Uploads)
            {
                Assert.Equal(handle, LastHandles[path]);
            }

            // Each failing decode was reported once to the sink (before or after Dispose), never
            // thrown at the render thread.
            (string Source, Exception Fault)[] faults = _faults.ToArray();
            Assert.Equal(_keys.Count(key => IsFailingKey(key.TexturePath)), faults.Length);
            Assert.All(faults, fault =>
            {
                Assert.Equal(StaticTerrainTextureCache.FaultSource, fault.Source);
                Assert.IsType<InvalidDataException>(fault.Fault);
            });
            Assert.Equal(
                _keys.Select(key => key.TexturePath).Where(IsFailingKey).Order(StringComparer.Ordinal),
                faults.Select(fault => fault.Fault.Message["corrupt ".Length..]).Order(StringComparer.Ordinal));
        }

        public string Describe()
        {
            return $"run {Index}: decodes={_decodes.Count} uploads={Uploads.Count} deleted={Deleted.Count} " +
                $"faults={_faults.Count} finishedAfterDispose={DecodesFinishedAfterDispose}";
        }

        // Sand decodes and the Stone top wait until after Dispose; the Stone top then fails.
        private static bool IsSlowKey(string path) => path.StartsWith("Sand-", StringComparison.Ordinal) || path == "Stone-Top.png";

        private static bool IsFailingKey(string path) => path.StartsWith("Stone-", StringComparison.Ordinal);

        // Runs on the thread pool. The texture's width identifies its key for the upload.
        private DecodedTexture? Decode(string path)
        {
            _decodes.AddOrUpdate(path, 1, (_, count) => count + 1);
            try
            {
                Assert.NotEqual(_renderThreadId, Environment.CurrentManagedThreadId);
                if (IsSlowKey(path))
                {
                    Assert.True(_slowDecodes.Wait(Timeout));
                }
                else
                {
                    Thread.Sleep(path.Length % 4);
                }

                if (_disposed)
                {
                    Interlocked.Increment(ref _decodesFinishedAfterDispose);
                }

                if (IsFailingKey(path))
                {
                    throw new InvalidDataException($"corrupt {path}");
                }

                int keyIndex = Array.FindIndex(_keys, key => key.TexturePath == path);
                return new DecodedTexture(keyIndex + 1, 1, new byte[(keyIndex + 1) * 4]);
            }
            finally
            {
                Interlocked.Increment(ref _decodesFinished);
            }
        }

        // Runs on the render thread.
        private int Upload(DecodedTexture texture)
        {
            Assert.Equal(_renderThreadId, Environment.CurrentManagedThreadId);
            if (_disposed)
            {
                UploadsAfterDispose++;
            }

            int handle = _nextHandle++;
            Uploads.Add((_keys[texture.Width - 1].TexturePath, handle));
            return handle;
        }
    }
}