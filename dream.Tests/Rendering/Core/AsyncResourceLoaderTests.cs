using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// GL-free: decode and upload are fakes, so the loader's threading, budget, fault and disposal
// rules are checked without a context. The game wires AnimatedModelData.Load / new AnimatedModel.
public class AsyncResourceLoaderTests
{
    private const string Source = "test-loader";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void TryGet_Is_False_Until_A_Pump_Uploads_The_Decoded_Resource()
    {
        using Harness harness = new();
        harness.Gate.Reset();

        harness.Loader.Request("grass");
        Assert.False(harness.Loader.TryGet("grass", out _));
        Assert.Equal(AsyncResourceState.Pending, harness.Loader.GetState("grass"));

        harness.Gate.Set();
        harness.WaitUploadable(1);

        // Decoded but not pumped: still not available.
        Assert.False(harness.Loader.TryGet("grass", out _));
        Assert.Equal(0, harness.Uploads);

        Assert.Equal(1, harness.Loader.PumpUploads(2));
        Assert.True(harness.Loader.TryGet("grass", out FakeResource? resource));
        Assert.Equal("grass", resource.Key);
        Assert.Equal(AsyncResourceState.Ready, harness.Loader.GetState("grass"));
        Assert.Equal(0, harness.Loader.PendingCount);
    }

    [Fact]
    public void Upload_Runs_Once_On_The_Pumping_Thread_And_Decode_Runs_Off_It()
    {
        using Harness harness = new();
        int pumpingThread = Environment.CurrentManagedThreadId;

        harness.Loader.Request("stone");
        harness.Loader.Request("stone");
        harness.WaitUploadable(1);
        harness.Loader.PumpUploads(4);
        harness.Loader.PumpUploads(4);
        harness.Loader.Request("stone");
        harness.Loader.PumpUploads(4);

        Assert.True(harness.Loader.TryGet("stone", out FakeResource? resource));
        Assert.Equal(1, harness.Decodes);
        Assert.Equal(1, harness.Uploads);
        Assert.Equal(pumpingThread, resource.UploadThreadId);
        Assert.NotEqual(pumpingThread, Assert.Single(harness.DecodeThreadIds));
    }

    [Fact]
    public void Faulting_Decode_Is_Reported_Once_Marked_Failed_And_Never_Retried()
    {
        using Harness harness = new(decodeFault: key => key == "broken");

        harness.Loader.Request("broken");
        harness.WaitUploadable(1);

        int uploaded = harness.Loader.PumpUploads(2);
        for (int frame = 0; frame < 5; frame++)
        {
            harness.Loader.Request("broken");
            uploaded += harness.Loader.PumpUploads(2);
        }

        Assert.Equal(0, uploaded);
        Assert.False(harness.Loader.TryGet("broken", out _));
        Assert.True(harness.Loader.IsFailed("broken"));
        Assert.Equal(1, harness.Decodes);
        Assert.Equal(0, harness.Uploads);
        Assert.Equal(1, harness.Faults.GetFaultCount(Source));
        Assert.IsType<InvalidOperationException>(harness.Faults.LastFault);
    }

    [Fact]
    public void Faulting_Upload_Is_Reported_Once_And_Marked_Failed()
    {
        using Harness harness = new(uploadFault: key => key == "bad-upload");

        harness.Loader.Request("bad-upload");
        harness.WaitUploadable(1);

        Assert.Equal(1, harness.Loader.PumpUploads(2));
        harness.Loader.Request("bad-upload");
        harness.Loader.PumpUploads(2);

        Assert.True(harness.Loader.IsFailed("bad-upload"));
        Assert.False(harness.Loader.TryGet("bad-upload", out _));
        Assert.Equal(1, harness.Decodes);
        Assert.Equal(1, harness.Faults.GetFaultCount(Source));
    }

    [Fact]
    public void PumpUploads_Respects_MaxPerFrame()
    {
        using Harness harness = new();
        string[] keys = ["a", "b", "c", "d", "e"];
        foreach (string key in keys)
        {
            harness.Loader.Request(key);
        }

        harness.WaitUploadable(keys.Length);

        Assert.Equal(2, harness.Loader.PumpUploads(2));
        Assert.Equal(2, harness.Uploads);
        Assert.Equal(0, harness.Loader.PumpUploads(0));
        Assert.Equal(2, harness.Uploads);
        Assert.Equal(2, harness.Loader.PumpUploads(2));
        Assert.Equal(1, harness.Loader.PumpUploads(2));
        Assert.Equal(0, harness.Loader.PumpUploads(2));
        Assert.Equal(5, harness.Uploads);
        Assert.All(keys, key => Assert.True(harness.Loader.TryGet(key, out _)));
    }

    [Fact]
    public void Decode_Faults_Do_Not_Use_Up_The_Upload_Budget()
    {
        using Harness harness = new(decodeFault: key => key.StartsWith("bad", StringComparison.Ordinal));
        harness.Loader.Request("bad-1");
        harness.Loader.Request("bad-2");
        harness.Loader.Request("good");
        harness.WaitUploadable(3);

        Assert.Equal(1, harness.Loader.PumpUploads(1));
        Assert.True(harness.Loader.TryGet("good", out _));
        Assert.Equal(2, harness.Faults.GetFaultCount(Source));
    }

    [Fact]
    public void Dispose_Disposes_Uploads_Once_And_Drops_A_Decode_That_Completes_Later()
    {
        Harness harness = new();
        harness.Loader.Request("ready");
        harness.WaitUploadable(1);
        harness.Loader.PumpUploads(1);
        Assert.True(harness.Loader.TryGet("ready", out FakeResource? ready));

        AsyncResourceHandle<FakeResource> instance = harness.Loader.RequestInstance("player");
        harness.WaitUploadable(1);
        harness.Loader.PumpUploads(1);
        Assert.True(instance.TryGet(out FakeResource? instanceResource));

        harness.Gate.Reset();
        harness.Loader.Request("late");
        AsyncResourceHandle<FakeResource> lateInstance = harness.Loader.RequestInstance("player");

        harness.Loader.Dispose();
        harness.Loader.Dispose();
        instance.Dispose();

        Assert.Equal(1, ready.DisposeCount);
        Assert.Equal(1, instanceResource.DisposeCount);
        Assert.Equal(AsyncResourceState.Disposed, instance.State);
        Assert.Equal(AsyncResourceState.Disposed, lateInstance.State);

        harness.Gate.Set();
        harness.WaitDecodes(4);
        Assert.Equal(0, harness.Loader.PumpUploads(8));
        harness.Loader.Request("late");
        Assert.Equal(0, harness.Loader.PumpUploads(8));

        Assert.Equal(2, harness.Uploads);
        Assert.False(harness.Loader.TryGet("ready", out _));
        Assert.False(harness.Loader.TryGet("late", out _));
        Assert.False(lateInstance.TryGet(out _));
        Assert.Equal(0, harness.Faults.FaultCount);
    }

    [Fact]
    public void Concurrent_Requests_For_One_Key_Decode_Once()
    {
        using Harness harness = new();
        const int Threads = 16;
        using Barrier barrier = new(Threads);
        Thread[] threads = new Thread[Threads];
        for (int i = 0; i < Threads; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int repeat = 0; repeat < 50; repeat++)
                {
                    harness.Loader.Request("shared");
                }
            });
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Timeout));
        }

        harness.WaitUploadable(1);
        harness.Loader.PumpUploads(Threads);

        Assert.Equal(1, harness.Decodes);
        Assert.Equal(1, harness.Uploads);
        Assert.True(harness.Loader.TryGet("shared", out _));
    }

    [Fact]
    public void Instance_Requests_Decode_And_Upload_Separately_And_Stay_Out_Of_The_Shared_Cache()
    {
        using Harness harness = new();
        AsyncResourceHandle<FakeResource>[] handles =
        [
            harness.Loader.RequestInstance("player"),
            harness.Loader.RequestInstance("player"),
            harness.Loader.RequestInstance("player")
        ];

        harness.WaitUploadable(3);
        Assert.All(handles, handle => Assert.False(handle.TryGet(out _)));
        Assert.Equal(3, harness.Loader.PumpUploads(8));

        FakeResource[] resources = handles.Select(handle =>
        {
            Assert.True(handle.TryGet(out FakeResource? resource));
            return resource;
        }).ToArray();

        Assert.Equal(3, harness.Decodes);
        Assert.Equal(3, resources.Distinct().Count());
        Assert.False(harness.Loader.TryGet("player", out _));
        Assert.Equal(3, harness.Loader.LiveInstanceCount);

        handles[0].Dispose();
        handles[0].Dispose();
        Assert.Equal(1, resources[0].DisposeCount);
        Assert.Equal(AsyncResourceState.Disposed, handles[0].State);
        Assert.Equal(2, harness.Loader.LiveInstanceCount);
        Assert.Equal(0, resources[1].DisposeCount);
    }

    [Fact]
    public void Remote_Prune_Discards_Pending_Instances_Of_Actors_That_Left()
    {
        using Harness harness = new();
        harness.Gate.Reset();
        Dictionary<int, AsyncResourceHandle<FakeResource>> cached = new()
        {
            [1] = harness.Loader.RequestInstance("player"),
            [2] = harness.Loader.RequestInstance("player"),
            [3] = harness.Loader.RequestInstance("player")
        };
        AsyncResourceHandle<FakeResource> leaving2 = cached[2];
        AsyncResourceHandle<FakeResource> leaving3 = cached[3];
        List<int> stale = [];

        int removed = RemotePlayerRenderer.PruneStaleEntries(cached, new HashSet<int> { 1 }, stale);

        Assert.Equal(2, removed);
        Assert.Equal([2, 3], stale.Order());
        Assert.Equal([1], cached.Keys);
        Assert.Equal(AsyncResourceState.Disposed, leaving2.State);
        Assert.Equal(AsyncResourceState.Disposed, leaving3.State);
        Assert.Equal(1, harness.Loader.PendingCount);

        harness.Gate.Set();
        harness.WaitDecodes(3);
        harness.WaitUploadable(1);
        Assert.Equal(1, harness.Loader.PumpUploads(8));
        Assert.Equal(0, harness.Loader.PumpUploads(8));

        Assert.Equal(1, harness.Uploads);
        Assert.True(cached[1].TryGet(out _));
        Assert.False(leaving2.TryGet(out _));
        Assert.Equal(0, harness.Faults.FaultCount);
    }

    [Fact]
    public void Requests_After_Dispose_Start_No_Decode()
    {
        Harness harness = new();
        harness.Loader.Dispose();

        harness.Loader.Request("x");
        AsyncResourceHandle<FakeResource> handle = harness.Loader.RequestInstance("x");

        Assert.Equal(AsyncResourceState.Disposed, handle.State);
        Assert.Equal(AsyncResourceState.Disposed, harness.Loader.GetState("x"));
        Assert.Equal(0, harness.Decodes);
        handle.Dispose();
    }

    [Theory]
    [InlineData("Actors")]
    [InlineData("ViewModels")]
    public void Renderers_Never_Load_A_Model_Synchronously(string folder)
    {
        string directory = Path.Combine(FindRepoRoot(), "dream", "Rendering", folder);
        string[] files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("new AnimatedModel(", source);
            Assert.DoesNotContain("CreatePlayerInstance()", source);
            Assert.DoesNotContain("ModelAssets.Get(", source);
        }
    }

    [Fact]
    public void ModelAssets_Has_No_Blocking_Getters()
    {
        string[] blocking = typeof(ModelAssets)
            .GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Where(member => member is PropertyInfo { PropertyType.Name: "AnimatedModel" } ||
                member is MethodInfo { ReturnType.Name: "AnimatedModel" })
            .Select(member => member.Name)
            .ToArray();

        Assert.Empty(blocking);
        Assert.Null(typeof(ModelAssets).GetMethod("Get", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(typeof(ModelAssets).GetMethod("CreatePlayerInstance", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void Cube_Scale_Resolution_Compares_Source_Paths_And_Never_Touches_The_Cache()
    {
        string render = File.ReadAllText(Path.Combine(FindRepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        int start = render.IndexOf("private static float ResolveCubeAssetScale(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = render.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = render[start..end];

        Assert.Contains("animation.SourcePath", body);
        Assert.DoesNotContain("ReferenceEquals", body);
        Assert.DoesNotContain("TryGet", body);
        Assert.DoesNotContain("ModelAssets.GrassCube)", body);
    }

    [Fact]
    public void PlayScene_Preloads_Models_And_Pumps_Uploads_Before_Drawing()
    {
        string scene = File.ReadAllText(Path.Combine(FindRepoRoot(), "dream", "Client", "Scenes", "PlayScene.cs"));
        int renderStart = scene.IndexOf("public void Render(FrameEventArgs args", StringComparison.Ordinal);
        Assert.True(renderStart >= 0);

        int pump = scene.IndexOf("ModelAssets.PumpUploads(", renderStart, StringComparison.Ordinal);
        int firstDraw = scene.IndexOf("_render.World.DrawWorld(", renderStart, StringComparison.Ordinal);
        Assert.True(pump > renderStart && pump < firstDraw, "Uploads must be pumped before the first draw.");
        Assert.Contains("ModelAssets.Preload(", scene);
        Assert.Contains("PreloadModels();", scene);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FakeData(string key)
    {
        public string Key { get; } = key;
    }

    private sealed class FakeResource(string key, int uploadThreadId) : IDisposable
    {
        private int _disposeCount;

        public string Key { get; } = key;

        public int UploadThreadId { get; } = uploadThreadId;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class Harness : IDisposable
    {
        private int _decodes;
        private int _uploads;

        public Harness(Func<string, bool>? decodeFault = null, Func<string, bool>? uploadFault = null)
        {
            Func<string, bool> failDecode = decodeFault ?? (_ => false);
            Func<string, bool> failUpload = uploadFault ?? (_ => false);
            Faults = new RenderFaultLog(_ => { });
            Loader = new AsyncResourceLoader<FakeData, FakeResource>(
                key =>
                {
                    DecodeThreadIds.Add(Environment.CurrentManagedThreadId);
                    try
                    {
                        Assert.True(Gate.Wait(Timeout), "decode gate timed out");
                        if (failDecode(key))
                        {
                            throw new InvalidOperationException($"decode failed: {key}");
                        }

                        return new FakeData(key);
                    }
                    finally
                    {
                        Interlocked.Increment(ref _decodes);
                    }
                },
                data =>
                {
                    Interlocked.Increment(ref _uploads);
                    if (failUpload(data.Key))
                    {
                        throw new InvalidOperationException($"upload failed: {data.Key}");
                    }

                    return new FakeResource(data.Key, Environment.CurrentManagedThreadId);
                },
                Faults,
                Source);
        }

        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public RenderFaultLog Faults { get; }

        public AsyncResourceLoader<FakeData, FakeResource> Loader { get; }

        public ConcurrentBag<int> DecodeThreadIds { get; } = [];

        public int Decodes => Volatile.Read(ref _decodes);

        public int Uploads => Volatile.Read(ref _uploads);

        public void WaitUploadable(int count) =>
            WaitFor(() => Loader.UploadableCount >= count, $"{count} finished decodes");

        public void WaitDecodes(int count) =>
            WaitFor(() => Decodes >= count, $"{count} decodes");

        public void Dispose()
        {
            // The gate is left undisposed: a decode the loader already dropped may still wait on it.
            Gate.Set();
            Loader.Dispose();
        }

        private static void WaitFor(Func<bool> condition, string what)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(stopwatch.Elapsed < Timeout, $"Timed out waiting for {what}.");
                Thread.Sleep(1);
            }
        }
    }
}
