using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Core;

// The frame phases timed on the GPU. They run one after another: GL allows only one active
// GL_TIME_ELAPSED query at a time, so sections can never nest.
public enum GpuTimerSection
{
    World = 0,
    Actors = 1,
    Hud = 2,
    Effects = 3,
    Shadows = 4,
    Post = 5
}

// The GL query calls GpuFrameTimer makes, behind a seam so the ring logic runs without a context.
public interface IGpuQueryApi
{
    int CreateQuery();

    void DeleteQuery(int query);

    void BeginTimeElapsed(int query);

    void EndTimeElapsed();

    void BeginPrimitivesGenerated(int query);

    void EndPrimitivesGenerated();

    // GL_QUERY_RESULT_AVAILABLE: never blocks.
    bool IsResultAvailable(int query);

    // GL_QUERY_RESULT. Only called after IsResultAvailable returned true, so it never stalls.
    long GetResult(int query);
}

// One resolved frame: GPU time per section and the primitives generated across the whole frame.
// FrameNumber is the frame the queries were issued in; 0 means no frame has resolved yet.
public readonly record struct GpuFrameTimings(
    long FrameNumber,
    double WorldMilliseconds,
    double ActorsMilliseconds,
    double HudMilliseconds,
    long PrimitivesGenerated,
    double EffectsMilliseconds = 0d,
    double ShadowMilliseconds = 0d,
    double PostMilliseconds = 0d)
{
    public bool HasValue => FrameNumber > 0;
}

// GPU timing without pipeline stalls. Each frame uses one slot of a small ring: a GL_TIME_ELAPSED
// query per section (world, actors, effects, HUD, shadows, post), issued sequentially, plus one GL_PRIMITIVES_GENERATED
// query around the whole frame. A slot is read back no sooner than MinReadLatencyFrames frames
// later, and only once every query in it reports QUERY_RESULT_AVAILABLE. If a slot is still
// pending when the ring comes back around to it, its result is dropped (MissedFrames) and the
// slot is reused, so a result that never arrives cannot block the timer.
// Render thread only. Steady-state calls allocate nothing.
public sealed class GpuFrameTimer : IDisposable
{
    public const int DefaultRingSize = 4;
    public const int MinReadLatencyFrames = 2;
    public const int SectionCount = 6;
    private const double NanosecondsPerMillisecond = 1_000_000d;

    private readonly IGpuQueryApi _api;
    private readonly Slot[] _slots;
    private long _frameNumber;
    private Slot? _current;
    private int _openSection = -1;
    private bool _primitivesOpen;
    private bool _disposed;

    public GpuFrameTimer(IGpuQueryApi api, int ringSize = DefaultRingSize)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (ringSize <= MinReadLatencyFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ringSize),
                ringSize,
                $"The ring needs more than {MinReadLatencyFrames} slots so a slot is not reused before it can be read.");
        }

        _api = api;
        _slots = new Slot[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            Slot slot = new();
            for (int section = 0; section < SectionCount; section++)
            {
                slot.TimeQueries[section] = api.CreateQuery();
            }

            slot.PrimitivesQuery = api.CreateQuery();
            _slots[i] = slot;
        }
    }

    public int RingSize => _slots.Length;

    // The newest frame whose queries have all resolved.
    public GpuFrameTimings Latest { get; private set; }

    // Frames whose results were dropped because they were still pending when their slot was reused.
    public long MissedFrames { get; private set; }

    public long ResolvedFrames { get; private set; }

    public bool IsFrameOpen => _current != null;

    // Reads back whatever older frames have resolved, then starts this frame's queries.
    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current != null)
        {
            EndFrame();
        }

        CollectResults(_frameNumber + 1);

        _frameNumber++;
        Slot slot = _slots[_frameNumber % _slots.Length];
        if (slot.Pending)
        {
            slot.Pending = false;
            MissedFrames++;
        }

        slot.FrameNumber = _frameNumber;
        Array.Clear(slot.Issued);
        slot.PrimitivesIssued = false;
        _current = slot;

        _api.BeginPrimitivesGenerated(slot.PrimitivesQuery);
        _primitivesOpen = true;
        slot.PrimitivesIssued = true;
    }

    // Ends any open section first, so sections run back to back and never nest. A section that
    // already ran this frame is not restarted (its query is spent until the slot is read).
    public void BeginSection(GpuTimerSection section)
    {
        Slot? slot = _current;
        int index = (int)section;
        if (slot == null || (uint)index >= SectionCount || slot.Issued[index])
        {
            return;
        }

        EndSection();
        _api.BeginTimeElapsed(slot.TimeQueries[index]);
        slot.Issued[index] = true;
        _openSection = index;
    }

    public void EndSection()
    {
        if (_openSection < 0)
        {
            return;
        }

        _api.EndTimeElapsed();
        _openSection = -1;
    }

    public void EndFrame()
    {
        Slot? slot = _current;
        if (slot == null)
        {
            return;
        }

        EndSection();
        if (_primitivesOpen)
        {
            _api.EndPrimitivesGenerated();
            _primitivesOpen = false;
        }

        slot.Pending = true;
        _current = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndFrame();
        foreach (Slot slot in _slots)
        {
            for (int section = 0; section < SectionCount; section++)
            {
                _api.DeleteQuery(slot.TimeQueries[section]);
            }

            _api.DeleteQuery(slot.PrimitivesQuery);
            slot.Pending = false;
        }
    }

    // Oldest first, so Latest only moves forward. GPU work completes in order, so the first slot
    // that is not ready means the newer ones are not ready either.
    private void CollectResults(long nextFrame)
    {
        for (int age = _slots.Length; age >= MinReadLatencyFrames; age--)
        {
            long frame = nextFrame - age;
            if (frame < 1)
            {
                continue;
            }

            Slot slot = _slots[frame % _slots.Length];
            if (!slot.Pending || slot.FrameNumber != frame)
            {
                continue;
            }

            if (!TryRead(slot))
            {
                return;
            }
        }
    }

    private bool TryRead(Slot slot)
    {
        for (int section = 0; section < SectionCount; section++)
        {
            if (slot.Issued[section] && !_api.IsResultAvailable(slot.TimeQueries[section]))
            {
                return false;
            }
        }

        if (slot.PrimitivesIssued && !_api.IsResultAvailable(slot.PrimitivesQuery))
        {
            return false;
        }

        Latest = new GpuFrameTimings(
            slot.FrameNumber,
            ReadMilliseconds(slot, GpuTimerSection.World),
            ReadMilliseconds(slot, GpuTimerSection.Actors),
            ReadMilliseconds(slot, GpuTimerSection.Hud),
            slot.PrimitivesIssued ? _api.GetResult(slot.PrimitivesQuery) : 0L,
            ReadMilliseconds(slot, GpuTimerSection.Effects),
            ReadMilliseconds(slot, GpuTimerSection.Shadows),
            ReadMilliseconds(slot, GpuTimerSection.Post));
        slot.Pending = false;
        ResolvedFrames++;
        return true;
    }

    private double ReadMilliseconds(Slot slot, GpuTimerSection section)
    {
        int index = (int)section;
        return slot.Issued[index]
            ? _api.GetResult(slot.TimeQueries[index]) / NanosecondsPerMillisecond
            : 0d;
    }

    private sealed class Slot
    {
        public readonly int[] TimeQueries = new int[SectionCount];
        public readonly bool[] Issued = new bool[SectionCount];
        public int PrimitivesQuery;
        public bool PrimitivesIssued;
        public long FrameNumber;
        public bool Pending;
    }
}

// The real GL implementation. GL 3.3 core has both query targets.
public sealed class GlGpuQueryApi : IGpuQueryApi
{
    public static GlGpuQueryApi Instance { get; } = new();

    private GlGpuQueryApi()
    {
    }

    public int CreateQuery() => GL.GenQuery();

    public void DeleteQuery(int query)
    {
        if (query != 0)
        {
            GL.DeleteQuery(query);
        }
    }

    public void BeginTimeElapsed(int query) => GL.BeginQuery(QueryTarget.TimeElapsed, query);

    public void EndTimeElapsed() => GL.EndQuery(QueryTarget.TimeElapsed);

    public void BeginPrimitivesGenerated(int query) => GL.BeginQuery(QueryTarget.PrimitivesGenerated, query);

    public void EndPrimitivesGenerated() => GL.EndQuery(QueryTarget.PrimitivesGenerated);

    public bool IsResultAvailable(int query)
    {
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResultAvailable, out int available);
        return available != 0;
    }

    public long GetResult(int query)
    {
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long result);
        return result;
    }
}
