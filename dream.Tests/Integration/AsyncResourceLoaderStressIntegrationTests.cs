using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Core;

namespace Dream.Tests.Integration;

// GFX-8's AsyncResourceLoader under load, GL-free: decode and upload are fakes that record every
// call. Eight requesting threads ask for shared keys and per-instance handles, poll them, and
// dispose handles at random points (some on their own thread, some handed to the render thread),
// while one "render thread" pumps with a budget of two uploads per frame and disposes the loader
// midway, then keeps pumping. Decodes have random delays, and some keys fault in decode or in
// upload. The loader's contract under all of that:
// - a decoded result is uploaded at most once, only on the render thread, never after Dispose;
// - every uploaded resource is disposed exactly once, by its handle's owner or by the loader;
// - a failed key is never decoded again;
// - nothing deadlocks (the whole run is bounded).
[Collection(TimingSensitiveCollection.Name)]
public class AsyncResourceLoaderStressIntegrationTests(ITestOutputHelper output)
{
    private const string Source = "stress-loader";
    private const int Requesters = 8;
    private const int OperationsPerRequester = 600;
    private const int SharedKeys = 48;
    private const int UploadBudget = 2;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ManyRequesters_OneBudgetedRenderThread_RandomHandleDisposal_AndLoaderDisposeMidway(int seed)
    {
        Fakes fakes = new();
        AsyncResourceLoader<FakeData, FakeResource> loader = new(fakes.Decode, fakes.Upload, fakes, Source);
        ConcurrentQueue<AsyncResourceHandle<FakeResource>> renderThreadDisposals = new();
        ConcurrentBag<AsyncResourceHandle<FakeResource>> allHandles = [];
        int completedOperations = 0;
        int requestersDone = 0;
        int instanceKeySource = 0;
        int disposedReady = 0;
        int disposedPending = 0;
        string?[] failures = new string?[Requesters + 1];
        Stopwatch elapsed = Stopwatch.StartNew();

        Thread render = new(() =>
        {
            try
            {
                fakes.RenderThreadId = Environment.CurrentManagedThreadId;
                int frames = 0;
                bool disposed = false;
                while (Volatile.Read(ref requestersDone) < Requesters || !disposed)
                {
                    frames++;
                    while (renderThreadDisposals.TryDequeue(out AsyncResourceHandle<FakeResource>? handle))
                    {
                        handle.Dispose();
                    }

                    int uploadsBefore = fakes.UploadCalls;
                    int uploaded = loader.PumpUploads(UploadBudget);
                    int calls = fakes.UploadCalls - uploadsBefore;
                    if (uploaded > UploadBudget || calls > UploadBudget || calls != uploaded)
                    {
                        throw new InvalidOperationException($"frame {frames}: pump returned {uploaded} with {calls} upload calls (budget {UploadBudget}).");
                    }

                    // Midway through the requesters' work, the render thread disposes the loader.
                    if (!disposed && Volatile.Read(ref completedOperations) > Requesters * OperationsPerRequester / 2)
                    {
                        loader.Dispose();
                        fakes.LoaderDisposed = true;
                        disposed = true;
                        if (loader.PendingCount != 0 || loader.LiveInstanceCount != 0)
                        {
                            throw new InvalidOperationException($"after Dispose: pending {loader.PendingCount}, live instances {loader.LiveInstanceCount}.");
                        }
                    }

                    if (elapsed.Elapsed > Bound)
                    {
                        throw new TimeoutException(
                            $"render thread ran past the bound at frame {frames}: uploads {fakes.Uploaded.Count}, upload calls {fakes.UploadCalls}, " +
                            $"pending {loader.PendingCount}, uploadable {loader.UploadableCount}, decodes {fakes.DecodesByKey.Count}, " +
                            $"in flight {Volatile.Read(ref fakes.DecodesInFlight)}, ops {Volatile.Read(ref completedOperations)}, " +
                            $"requesters done {Volatile.Read(ref requestersDone)}, disposed {disposed}, pool threads {ThreadPool.ThreadCount}, " +
                            $"pool queue {ThreadPool.PendingWorkItemCount}");
                    }

                    Thread.Sleep(0);
                }

                // A few more frames after everything: still nothing is uploaded.
                for (int i = 0; i < 20; i++)
                {
                    if (loader.PumpUploads(UploadBudget) != 0)
                    {
                        throw new InvalidOperationException("pump uploaded after Dispose.");
                    }

                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                failures[Requesters] = ex.ToString();
            }
        })
        {
            IsBackground = true,
            Name = "fake-render-thread"
        };

        Thread[] requesters = new Thread[Requesters];
        for (int r = 0; r < Requesters; r++)
        {
            int index = r;
            requesters[r] = new Thread(() =>
            {
                Random random = new(seed * 1000 + index);
                List<AsyncResourceHandle<FakeResource>> mine = [];
                try
                {
                    for (int op = 0; op < OperationsPerRequester; op++)
                    {
                        if (op == OperationsPerRequester / 2)
                        {
                            // The loader is disposed just after half of all operations are done.
                            // Under a loaded machine the requesters can get there before the render
                            // thread uploaded anything, so each requests one handle for a key that
                            // never faults and waits (bounded) until it is ready (or the loader was
                            // already disposed by a requester that got past this point first), then
                            // disposes one of its ready handles.
                            int number;
                            do
                            {
                                number = Interlocked.Increment(ref instanceKeySource);
                            }
                            while (Fakes.DecodeFaultsFor(number) || Fakes.UploadFaultsFor(number));

                            AsyncResourceHandle<FakeResource> gate = loader.RequestInstance($"inst-{number}");
                            mine.Add(gate);
                            allHandles.Add(gate);
                            if (!SpinWait.SpinUntil(
                                    () => gate.State is AsyncResourceState.Ready or AsyncResourceState.Disposed ||
                                        Volatile.Read(ref failures[Requesters]) != null,
                                    Bound))
                            {
                                throw new TimeoutException(
                                    $"no handle became ready before the dispose point (uploads {fakes.Uploaded.Count}, upload calls {fakes.UploadCalls}, " +
                                    $"pending {loader.PendingCount}, uploadable {loader.UploadableCount}, decodes {fakes.DecodesByKey.Count}, " +
                                    $"in flight {Volatile.Read(ref fakes.DecodesInFlight)}).");
                            }

                            int ready = mine.FindIndex(handle => handle.State == AsyncResourceState.Ready);
                            if (ready >= 0)
                            {
                                AsyncResourceHandle<FakeResource> readyHandle = mine[ready];
                                mine.RemoveAt(ready);
                                Interlocked.Increment(ref disposedReady);
                                readyHandle.Dispose();
                            }
                        }

                        string sharedKey = $"shared-{random.Next(SharedKeys)}";
                        switch (random.Next(8))
                        {
                            case 0:
                            case 1:
                                loader.Request(sharedKey);
                                break;
                            case 2:
                                if (loader.TryGet(sharedKey, out FakeResource? shared) && shared.Key != sharedKey)
                                {
                                    throw new InvalidOperationException($"TryGet({sharedKey}) returned {shared.Key}.");
                                }

                                // A failed key asked for again must stay failed and undecoded.
                                if (loader.IsFailed(sharedKey))
                                {
                                    loader.Request(sharedKey);
                                }

                                break;
                            case 3:
                            case 4:
                                AsyncResourceHandle<FakeResource> handle = loader.RequestInstance($"inst-{Interlocked.Increment(ref instanceKeySource)}");
                                mine.Add(handle);
                                allHandles.Add(handle);
                                break;
                            case 5:
                                foreach (AsyncResourceHandle<FakeResource> candidate in mine)
                                {
                                    if (candidate.TryGet(out FakeResource? resource) && resource.Key != candidate.Key)
                                    {
                                        throw new InvalidOperationException($"handle {candidate.Key} returned {resource.Key}.");
                                    }
                                }

                                break;
                            default:
                                if (mine.Count > 0)
                                {
                                    int pick = random.Next(mine.Count);
                                    AsyncResourceHandle<FakeResource> victim = mine[pick];
                                    mine.RemoveAt(pick);
                                    AsyncResourceState before = victim.State;
                                    if (before == AsyncResourceState.Ready)
                                    {
                                        Interlocked.Increment(ref disposedReady);
                                    }
                                    else if (before == AsyncResourceState.Pending)
                                    {
                                        Interlocked.Increment(ref disposedPending);
                                    }

                                    // Pending or ready, whatever it is now; half on this thread,
                                    // half on the render thread as the game does.
                                    if (random.Next(2) == 0)
                                    {
                                        victim.Dispose();
                                        if (random.Next(4) == 0)
                                        {
                                            victim.Dispose();
                                        }
                                    }
                                    else
                                    {
                                        renderThreadDisposals.Enqueue(victim);
                                    }
                                }

                                break;
                        }

                        Interlocked.Increment(ref completedOperations);
                        // Paced so decodes finish and uploads happen while requests keep coming.
                        if (random.Next(4) == 0)
                        {
                            Thread.Sleep(random.Next(3));
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures[index] = ex.ToString();
                }
                finally
                {
                    Interlocked.Increment(ref requestersDone);
                }
            })
            {
                IsBackground = true,
                Name = $"requester-{r}"
            };
        }

        render.Start();
        foreach (Thread requester in requesters)
        {
            requester.Start();
        }

        foreach (Thread requester in requesters)
        {
            Assert.True(
                requester.Join(Bound),
                $"A requester did not finish: deadlock. Failures so far: {string.Join(" | ", failures.Where(failure => failure != null))}");
        }

        Assert.True(render.Join(Bound), "The render thread did not finish: deadlock.");
        Assert.All(failures, failure => Assert.Null(failure));
        Assert.True(loader.IsDisposed);

        // Owners dispose whatever they still hold, after the loader (idempotent either way).
        foreach (AsyncResourceHandle<FakeResource> handle in allHandles)
        {
            handle.Dispose();
            Assert.Equal(AsyncResourceState.Disposed, handle.State);
            Assert.False(handle.TryGet(out _));
        }

        // Decodes still running at Dispose finish on the pool and are dropped.
        SpinWait.SpinUntil(() => Volatile.Read(ref fakes.DecodesInFlight) == 0, Bound);
        Assert.Equal(0, Volatile.Read(ref fakes.DecodesInFlight));
        loader.Dispose();

        Assert.Empty(fakes.Violations);
        foreach ((string key, int decodes) in fakes.DecodesByKey)
        {
            // Shared keys are requested many times and instance keys are unique: each is decoded
            // at most once, so a failed key is never retried.
            Assert.True(decodes == 1, $"{key} decoded {decodes} times.");
        }

        foreach ((long decodeId, int uploads) in fakes.UploadsByDecode)
        {
            Assert.True(uploads == 1, $"decode {decodeId} uploaded {uploads} times.");
        }

        foreach (FakeResource resource in fakes.Uploaded)
        {
            Assert.True(resource.DisposeCount == 1, $"{resource.Key} (decode {resource.DecodeId}) disposed {resource.DisposeCount} times.");
        }

        // Every decode fault of a shared key is reported once, at most.
        foreach (IGrouping<string, string> faulted in fakes.Reports.Where(report => report.StartsWith("decode:shared-", StringComparison.Ordinal)).GroupBy(report => report))
        {
            Assert.True(faulted.Count() == 1, $"{faulted.Key} reported {faulted.Count()} times.");
        }

        output.WriteLine(
            $"seed {seed}: decodes {fakes.DecodesByKey.Count}, uploads {fakes.Uploaded.Count}, upload faults {fakes.UploadFaults}, " +
            $"fault reports {fakes.Reports.Count}, handles {allHandles.Count} (disposed ready {disposedReady}, pending {disposedPending}), " +
            $"{elapsed.ElapsedMilliseconds} ms");
        Assert.True(fakes.Uploaded.Count > 0, "Nothing was uploaded.");
        Assert.True(disposedReady > 0, "No handle was disposed while ready.");
        Assert.True(disposedPending > 0, "No handle was disposed while pending.");
        Assert.True(fakes.Reports.Count > 0, "No fault was exercised.");
    }

    private sealed class FakeData(string key, long decodeId)
    {
        public string Key { get; } = key;

        public long DecodeId { get; } = decodeId;
    }

    private sealed class FakeResource(string key, long decodeId) : IDisposable
    {
        private int _disposeCount;

        public string Key { get; } = key;

        public long DecodeId { get; } = decodeId;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class Fakes : IRenderFaultSink
    {
        private long _decodeIds;
        private int _uploadCalls;
        private int _uploadFaults;
        public int DecodesInFlight;

        public volatile int RenderThreadId;

        public volatile bool LoaderDisposed;

        public ConcurrentDictionary<string, int> DecodesByKey { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<long, int> UploadsByDecode { get; } = new();

        public ConcurrentBag<FakeResource> Uploaded { get; } = [];

        public ConcurrentBag<string> Violations { get; } = [];

        public ConcurrentBag<string> Reports { get; } = [];

        public int UploadCalls => Volatile.Read(ref _uploadCalls);

        public int UploadFaults => Volatile.Read(ref _uploadFaults);

        public FakeData Decode(string key)
        {
            Interlocked.Increment(ref DecodesInFlight);
            try
            {
                DecodesByKey.AddOrUpdate(key, 1, (_, count) => count + 1);
                long id = Interlocked.Increment(ref _decodeIds);
                int delay = Random.Shared.Next(6);
                if (delay > 0)
                {
                    Thread.Sleep(delay - 1);
                }

                if (DecodeFaultsFor(KeyNumber(key)))
                {
                    throw new InvalidDataException($"decode:{key}");
                }

                return new FakeData(key, id);
            }
            finally
            {
                Interlocked.Decrement(ref DecodesInFlight);
            }
        }

        public FakeResource Upload(FakeData data)
        {
            Interlocked.Increment(ref _uploadCalls);
            if (Environment.CurrentManagedThreadId != RenderThreadId)
            {
                Violations.Add($"{data.Key} uploaded off the render thread.");
            }

            if (LoaderDisposed)
            {
                Violations.Add($"{data.Key} uploaded after the loader was disposed.");
            }

            UploadsByDecode.AddOrUpdate(data.DecodeId, 1, (_, count) => count + 1);
            if (UploadFaultsFor(KeyNumber(data.Key)))
            {
                Interlocked.Increment(ref _uploadFaults);
                throw new InvalidOperationException($"upload:{data.Key}");
            }

            FakeResource resource = new(data.Key, data.DecodeId);
            Uploaded.Add(resource);
            return resource;
        }

        public void Report(string source, Exception exception)
        {
            if (source != Source)
            {
                Violations.Add($"fault reported for source {source}.");
            }

            Reports.Add(exception.Message);
        }

        public static bool DecodeFaultsFor(int keyNumber) => keyNumber % 7 == 3;

        public static bool UploadFaultsFor(int keyNumber) => keyNumber % 11 == 6;

        private static int KeyNumber(string key)
        {
            return int.Parse(key.AsSpan(key.IndexOf('-') + 1));
        }
    }
}
