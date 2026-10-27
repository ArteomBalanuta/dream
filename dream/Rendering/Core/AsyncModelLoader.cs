using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Dream.Rendering.Core;

public enum AsyncResourceState
{
    NotRequested,
    Pending,
    Ready,
    Failed,
    Disposed
}

// A per-instance request from AsyncResourceLoader.RequestInstance. The caller owns it: Dispose
// releases the uploaded resource, or discards the decode if it has not been uploaded yet. Poll
// TryGet every frame and skip drawing until it returns true. Dispose on the render thread.
public abstract class AsyncResourceHandle<TResource> : IDisposable
    where TResource : class, IDisposable
{
    private protected AsyncResourceHandle(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public abstract AsyncResourceState State { get; }

    public bool IsReady => State == AsyncResourceState.Ready;

    public bool IsFailed => State == AsyncResourceState.Failed;

    public abstract bool TryGet([NotNullWhen(true)] out TResource? resource);

    public abstract void Dispose();
}

// Decodes on the thread pool and uploads on the thread that calls PumpUploads (the render thread
// in the game), so a draw never parses or uploads an asset itself.
//
// - Request(key) starts one decode per key; TryGet(key) never blocks and returns the shared
//   resource once a pump has uploaded it. The loader owns shared resources.
// - RequestInstance(key) starts a separate decode on every call and returns a handle that its
//   caller owns (per-instance models need their own pose).
// - A decode or upload fault is reported once to the fault sink and the key (or handle) is
//   marked Failed for good; it is never retried.
// - Dispose releases every shared resource and every instance handle still alive, observes
//   pending decodes, and drops any decode that finishes afterwards. Handle disposal is idempotent,
//   so an owner disposing its handle before or after the loader never disposes a resource twice.
public sealed class AsyncResourceLoader<TData, TResource> : IDisposable
    where TResource : class, IDisposable
{
    private readonly object _sync = new();
    private readonly Func<string, TData> _decode;
    private readonly Func<TData, TResource> _upload;
    private readonly IRenderFaultSink _faultSink;
    private readonly string _faultSource;
    private readonly Dictionary<string, Entry> _shared = new(StringComparer.Ordinal);
    private readonly ResourceCache<TResource> _uploaded = new();
    private readonly List<Entry> _pending = [];
    private readonly HashSet<Entry> _liveInstances = [];
    private bool _disposed;

    public AsyncResourceLoader(
        Func<string, TData> decode,
        Func<TData, TResource> upload,
        IRenderFaultSink faultSink,
        string faultSource)
    {
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(faultSink);
        ArgumentException.ThrowIfNullOrWhiteSpace(faultSource);
        _decode = decode;
        _upload = upload;
        _faultSink = faultSink;
        _faultSource = faultSource;
    }

    // Decodes that have not been uploaded yet, shared and per-instance.
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    // Pending decodes that have finished (or faulted) and wait for the next pump.
    public int UploadableCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].Decode is { IsCompleted: true })
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public int LiveInstanceCount
    {
        get
        {
            lock (_sync)
            {
                return _liveInstances.Count;
            }
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    // Starts the shared decode for key unless it was already requested. Safe from any thread.
    public void Request(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_sync)
        {
            if (_disposed || _shared.ContainsKey(key))
            {
                return;
            }

            Entry entry = new(this, key, isInstance: false);
            _shared.Add(key, entry);
            StartDecode(entry);
        }
    }

    // Starts a decode of its own for every call. The returned handle is owned by the caller.
    public AsyncResourceHandle<TResource> RequestInstance(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Entry entry = new(this, key, isInstance: true);
        lock (_sync)
        {
            if (_disposed)
            {
                entry.StateUnsafe = AsyncResourceState.Disposed;
                return entry;
            }

            _liveInstances.Add(entry);
            StartDecode(entry);
        }

        return entry;
    }

    public bool TryGet(string key, [NotNullWhen(true)] out TResource? resource)
    {
        return _uploaded.TryGetValue(key, out resource);
    }

    public AsyncResourceState GetState(string key)
    {
        lock (_sync)
        {
            if (_shared.TryGetValue(key, out Entry? entry))
            {
                return entry.StateUnsafe;
            }

            return _disposed ? AsyncResourceState.Disposed : AsyncResourceState.NotRequested;
        }
    }

    public bool IsFailed(string key) => GetState(key) == AsyncResourceState.Failed;

    // Uploads up to maxPerFrame finished decodes on the calling thread and returns how many it
    // uploaded. Decode faults are reported and do not count against the budget. Never throws
    // for a decode or upload fault.
    public int PumpUploads(int maxPerFrame)
    {
        int uploaded = 0;
        while (uploaded < maxPerFrame)
        {
            Entry? entry = null;
            TData? data = default;
            Exception? fault = null;
            lock (_sync)
            {
                if (_disposed)
                {
                    return uploaded;
                }

                for (int i = 0; i < _pending.Count; i++)
                {
                    Entry candidate = _pending[i];
                    if (candidate.Decode is not { IsCompleted: true })
                    {
                        continue;
                    }

                    _pending.RemoveAt(i);
                    entry = candidate;
                    if (!BackgroundWork.TryTakeCompleted(ref candidate.Decode, out data, out fault) && fault == null)
                    {
                        fault = new InvalidOperationException($"Decode of '{candidate.Key}' finished without a result.");
                    }

                    if (fault == null && data is null)
                    {
                        fault = new InvalidOperationException($"Decode of '{candidate.Key}' returned null.");
                    }

                    if (fault != null)
                    {
                        candidate.StateUnsafe = AsyncResourceState.Failed;
                    }

                    break;
                }
            }

            if (entry == null)
            {
                return uploaded;
            }

            if (fault != null)
            {
                _faultSink.Report(_faultSource, fault);
                continue;
            }

            uploaded++;
            TResource resource;
            try
            {
                resource = _upload(data!);
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (entry.StateUnsafe == AsyncResourceState.Pending)
                    {
                        entry.StateUnsafe = AsyncResourceState.Failed;
                    }
                }

                _faultSink.Report(_faultSource, ex);
                continue;
            }

            if (!Publish(entry, resource))
            {
                // The loader or the handle was disposed during the upload; nobody else owns it.
                DisposeQuietly(resource);
            }
        }

        return uploaded;
    }

    public void Dispose()
    {
        List<Task<TData>>? pendingDecodes = null;
        List<TResource>? instanceResources = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Entry entry in _pending)
            {
                if (entry.Decode != null)
                {
                    (pendingDecodes ??= []).Add(entry.Decode);
                    entry.Decode = null;
                }
            }

            foreach (Entry entry in _shared.Values)
            {
                entry.StateUnsafe = AsyncResourceState.Disposed;
            }

            foreach (Entry entry in _liveInstances)
            {
                entry.StateUnsafe = AsyncResourceState.Disposed;
                if (entry.Resource != null)
                {
                    (instanceResources ??= []).Add(entry.Resource);
                    entry.Resource = null;
                }
            }

            _pending.Clear();
            _shared.Clear();
            _liveInstances.Clear();
        }

        if (pendingDecodes != null)
        {
            foreach (Task<TData> pending in pendingDecodes)
            {
                BackgroundWork.ObserveAndForget(pending, _faultSink, _faultSource);
            }
        }

        List<Exception>? failures = null;
        try
        {
            _uploaded.DisposeAll();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        if (instanceResources != null)
        {
            foreach (TResource resource in instanceResources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
        }

        if (failures == null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    // Caller holds _sync.
    private void StartDecode(Entry entry)
    {
        string key = entry.Key;
        Func<string, TData> decode = _decode;
        entry.StateUnsafe = AsyncResourceState.Pending;
        entry.Decode = Task.Run(() => decode(key));
        _pending.Add(entry);
    }

    private bool Publish(Entry entry, TResource resource)
    {
        lock (_sync)
        {
            if (_disposed || entry.StateUnsafe != AsyncResourceState.Pending)
            {
                return false;
            }

            if (!entry.IsInstance && !_uploaded.TryAdd(entry.Key, resource))
            {
                return false;
            }

            entry.Resource = resource;
            entry.StateUnsafe = AsyncResourceState.Ready;
            return true;
        }
    }

    private void ReleaseInstance(Entry entry)
    {
        TResource? resource;
        Task<TData>? decode;
        lock (_sync)
        {
            if (entry.StateUnsafe == AsyncResourceState.Disposed)
            {
                return;
            }

            entry.StateUnsafe = AsyncResourceState.Disposed;
            _liveInstances.Remove(entry);
            _pending.Remove(entry);
            resource = entry.Resource;
            entry.Resource = null;
            decode = entry.Decode;
            entry.Decode = null;
        }

        BackgroundWork.ObserveAndForget(decode, _faultSink, _faultSource);
        resource?.Dispose();
    }

    private void DisposeQuietly(TResource resource)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            _faultSink.Report(_faultSource, ex);
        }
    }

    private sealed class Entry : AsyncResourceHandle<TResource>
    {
        private readonly AsyncResourceLoader<TData, TResource> _owner;

        public Entry(AsyncResourceLoader<TData, TResource> owner, string key, bool isInstance)
            : base(key)
        {
            _owner = owner;
            IsInstance = isInstance;
        }

        public bool IsInstance { get; }

        // The fields below are guarded by the owner's _sync.
        public Task<TData>? Decode;

        public TResource? Resource;

        public AsyncResourceState StateUnsafe = AsyncResourceState.NotRequested;

        public override AsyncResourceState State
        {
            get
            {
                lock (_owner._sync)
                {
                    return StateUnsafe;
                }
            }
        }

        public override bool TryGet([NotNullWhen(true)] out TResource? resource)
        {
            lock (_owner._sync)
            {
                resource = StateUnsafe == AsyncResourceState.Ready ? Resource : null;
                return resource != null;
            }
        }

        public override void Dispose()
        {
            if (!IsInstance)
            {
                // Shared entries never leave the loader; the loader's Dispose releases them.
                return;
            }

            _owner.ReleaseInstance(this);
        }
    }
}
