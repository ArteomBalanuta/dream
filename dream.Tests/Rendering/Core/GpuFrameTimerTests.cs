using Xunit;
using Dream.Rendering.Core;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Core;

public class GpuFrameTimerTests
{
    [Fact]
    public void Results_Arrive_MinReadLatency_Frames_Later()
    {
        FakeGpuQueryApi api = new() { AvailableAfterFrames = 0 };
        using GpuFrameTimer timer = new(api);

        RunFrame(timer, api, worldNs: 1_000_000, actorsNs: 2_000_000, hudNs: 500_000, primitives: 100);
        Assert.False(timer.Latest.HasValue);
        RunFrame(timer, api, worldNs: 3_000_000, actorsNs: 0, hudNs: 0, primitives: 200);
        Assert.False(timer.Latest.HasValue);

        // Frame 3 begins: frame 1 is now two frames old and is read back.
        RunFrame(timer, api, worldNs: 5_000_000, actorsNs: 0, hudNs: 0, primitives: 300);
        Assert.Equal(1, timer.Latest.FrameNumber);
        Assert.Equal(1.0, timer.Latest.WorldMilliseconds, 9);
        Assert.Equal(2.0, timer.Latest.ActorsMilliseconds, 9);
        Assert.Equal(0.5, timer.Latest.HudMilliseconds, 9);
        Assert.Equal(100, timer.Latest.PrimitivesGenerated);

        RunFrame(timer, api, 0, 0, 0, 0);
        Assert.Equal(2, timer.Latest.FrameNumber);
        Assert.Equal(3.0, timer.Latest.WorldMilliseconds, 9);
        Assert.Equal(200, timer.Latest.PrimitivesGenerated);
        Assert.Equal(GpuFrameTimer.MinReadLatencyFrames, 2);
    }

    [Fact]
    public void Never_Reads_A_Result_Before_It_Is_Available()
    {
        // The GPU runs three frames behind: frame 1's queries are first ready when frame 5 begins.
        FakeGpuQueryApi api = new() { AvailableAfterFrames = 3 };
        using GpuFrameTimer timer = new(api);

        RunFrame(timer, api, 1_000_000, 1_000_000, 1_000_000, 10);
        RunFrame(timer, api, 2_000_000, 2_000_000, 2_000_000, 20);
        RunFrame(timer, api, 3_000_000, 3_000_000, 3_000_000, 30);
        RunFrame(timer, api, 4_000_000, 4_000_000, 4_000_000, 40);
        Assert.False(timer.Latest.HasValue);
        Assert.True(api.AvailabilityChecks > 0, "The timer should have polled the frame-1 queries.");

        RunFrame(timer, api, 5_000_000, 5_000_000, 5_000_000, 50);
        Assert.Equal(1, timer.Latest.FrameNumber);
        Assert.Equal(1.0, timer.Latest.HudMilliseconds, 9);
        Assert.Equal(10, timer.Latest.PrimitivesGenerated);

        for (int i = 0; i < 20; i++)
        {
            RunFrame(timer, api, 1_000_000, 1_000_000, 1_000_000, 1);
        }

        Assert.Equal(0, api.ResultReadsBeforeAvailable);
        Assert.Equal(0, timer.MissedFrames);
        Assert.Equal(api.CurrentFrame - 4, timer.ResolvedFrames);
    }

    [Fact]
    public void Survives_A_Result_That_Never_Arrives()
    {
        FakeGpuQueryApi api = new() { AvailableAfterFrames = 0 };
        using GpuFrameTimer timer = new(api);

        // Frame 1's world query never becomes available.
        timer.BeginFrame();
        api.CurrentFrame++;
        timer.BeginSection(GpuTimerSection.World);
        int lostQuery = api.ActiveTimeQuery;
        api.NeverAvailable.Add(lostQuery);
        timer.EndFrame();

        for (int i = 0; i < 8; i++)
        {
            RunFrame(timer, api, 2_000_000, 0, 0, 7);
        }

        Assert.Equal(1, timer.MissedFrames);
        Assert.True(timer.Latest.FrameNumber > 1);
        Assert.Equal(2.0, timer.Latest.WorldMilliseconds, 9);
        Assert.Equal(0, api.ResultReadsBeforeAvailable);

        // The slot was reused: its query object now carries a fresh, readable result.
        api.NeverAvailable.Clear();
        RunFrame(timer, api, 0, 0, 0, 0);
        RunFrame(timer, api, 0, 0, 0, 0);
        Assert.Equal(1, timer.MissedFrames);
    }

    [Fact]
    public void Sections_Never_Nest_And_Run_At_Most_Once_Per_Frame()
    {
        FakeGpuQueryApi api = new() { AvailableAfterFrames = 0 };
        using GpuFrameTimer timer = new(api);

        timer.BeginFrame();
        timer.BeginSection(GpuTimerSection.World);
        timer.BeginSection(GpuTimerSection.Actors);
        timer.BeginSection(GpuTimerSection.Actors);
        timer.BeginSection(GpuTimerSection.World);
        timer.BeginSection(GpuTimerSection.Hud);
        timer.EndFrame();

        Assert.Equal(0, api.NestedTimeElapsedBegins);
        Assert.Equal(3, api.TimeElapsedBegins);
        Assert.Equal(3, api.TimeElapsedEnds);
        Assert.Null(api.ActiveTimeQueryOrNull);
        Assert.Null(api.ActivePrimitivesQueryOrNull);

        // A frame that is never ended explicitly is closed by the next BeginFrame.
        timer.BeginFrame();
        timer.BeginSection(GpuTimerSection.World);
        timer.BeginFrame();
        Assert.Equal(0, api.NestedTimeElapsedBegins);
        Assert.Equal(0, api.NestedPrimitivesBegins);
        Assert.Equal(api.TimeElapsedBegins, api.TimeElapsedEnds);
        timer.EndFrame();
    }

    [Fact]
    public void Sections_Outside_A_Frame_Issue_No_Queries()
    {
        FakeGpuQueryApi api = new();
        using GpuFrameTimer timer = new(api);

        timer.BeginSection(GpuTimerSection.World);
        timer.EndSection();
        timer.EndFrame();

        Assert.Equal(0, api.TimeElapsedBegins);
        Assert.Equal(0, api.TimeElapsedEnds);
    }

    [Fact]
    public void Dispose_Deletes_Every_Query_And_Closes_An_Open_Frame()
    {
        FakeGpuQueryApi api = new();
        GpuFrameTimer timer = new(api, ringSize: 3);
        Assert.Equal(3 * 4, api.Created.Count);

        timer.BeginFrame();
        timer.BeginSection(GpuTimerSection.Hud);
        timer.Dispose();
        timer.Dispose();

        Assert.Null(api.ActiveTimeQueryOrNull);
        Assert.Null(api.ActivePrimitivesQueryOrNull);
        Assert.Equal(api.Created.OrderBy(q => q), api.Deleted.OrderBy(q => q));
        Assert.Throws<ObjectDisposedException>(timer.BeginFrame);
    }

    [Fact]
    public void Ring_Must_Be_Longer_Than_The_Read_Latency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuFrameTimer(new FakeGpuQueryApi(), ringSize: 2));
    }

    [Fact]
    public void Steady_State_Frames_Allocate_Nothing()
    {
        FakeGpuQueryApi api = new() { AvailableAfterFrames = 2 };
        using GpuFrameTimer timer = new(api);

        AllocationAssert.AtMost(0, () =>
        {
            timer.BeginFrame();
            timer.BeginSection(GpuTimerSection.World);
            timer.BeginSection(GpuTimerSection.Actors);
            timer.BeginSection(GpuTimerSection.Hud);
            timer.EndFrame();
            if (timer.Latest.FrameNumber < 0)
            {
                throw new InvalidOperationException();
            }
        });
    }

    private static void RunFrame(GpuFrameTimer timer, FakeGpuQueryApi api, long worldNs, long actorsNs, long hudNs, long primitives)
    {
        timer.BeginFrame();
        api.CurrentFrame++;
        api.NextPrimitives = primitives;
        timer.BeginSection(GpuTimerSection.World);
        api.NextTimeElapsed = worldNs;
        timer.BeginSection(GpuTimerSection.Actors);
        api.NextTimeElapsed = actorsNs;
        timer.BeginSection(GpuTimerSection.Hud);
        api.NextTimeElapsed = hudNs;
        timer.EndFrame();
    }

    // Records each query's result when it ends and makes it available AvailableAfterFrames frames
    // after the frame that issued it, as a GPU running behind the CPU would.
    private sealed class FakeGpuQueryApi : IGpuQueryApi
    {
        private readonly Dictionary<int, (long Value, long Frame)> _results = new();
        private int _nextQuery = 1;

        public int AvailableAfterFrames { get; init; } = 1;
        public long CurrentFrame { get; set; }
        public long NextTimeElapsed { get; set; }
        public long NextPrimitives { get; set; }
        public List<int> Created { get; } = [];
        public List<int> Deleted { get; } = [];
        public HashSet<int> NeverAvailable { get; } = [];
        public int? ActiveTimeQueryOrNull { get; private set; }
        public int? ActivePrimitivesQueryOrNull { get; private set; }
        public int ActiveTimeQuery => ActiveTimeQueryOrNull ?? throw new InvalidOperationException("No active query.");
        public int TimeElapsedBegins { get; private set; }
        public int TimeElapsedEnds { get; private set; }
        public int NestedTimeElapsedBegins { get; private set; }
        public int NestedPrimitivesBegins { get; private set; }
        public int AvailabilityChecks { get; private set; }
        public int ResultReadsBeforeAvailable { get; private set; }

        public int CreateQuery()
        {
            int query = _nextQuery++;
            Created.Add(query);
            return query;
        }

        public void DeleteQuery(int query) => Deleted.Add(query);

        public void BeginTimeElapsed(int query)
        {
            if (ActiveTimeQueryOrNull != null)
            {
                NestedTimeElapsedBegins++;
            }

            TimeElapsedBegins++;
            ActiveTimeQueryOrNull = query;
            _results.Remove(query);
            // Re-issuing a query replaces the lost result with a fresh one.
            NeverAvailable.Remove(query);
        }

        // The value set between Begin and End is the section's GPU time; see RunFrame.
        public void EndTimeElapsed()
        {
            int query = ActiveTimeQuery;
            TimeElapsedEnds++;
            _results[query] = (NextTimeElapsed, CurrentFrame);
            NextTimeElapsed = 0;
            ActiveTimeQueryOrNull = null;
        }

        public void BeginPrimitivesGenerated(int query)
        {
            if (ActivePrimitivesQueryOrNull != null)
            {
                NestedPrimitivesBegins++;
            }

            ActivePrimitivesQueryOrNull = query;
            _results.Remove(query);
        }

        public void EndPrimitivesGenerated()
        {
            int query = ActivePrimitivesQueryOrNull ?? throw new InvalidOperationException("No active primitives query.");
            _results[query] = (NextPrimitives, CurrentFrame);
            ActivePrimitivesQueryOrNull = null;
        }

        public bool IsResultAvailable(int query)
        {
            AvailabilityChecks++;
            return IsAvailable(query);
        }

        public long GetResult(int query)
        {
            if (!IsAvailable(query))
            {
                ResultReadsBeforeAvailable++;
                return -1;
            }

            return _results[query].Value;
        }

        private bool IsAvailable(int query) =>
            !NeverAvailable.Contains(query) &&
            _results.TryGetValue(query, out (long Value, long Frame) result) &&
            CurrentFrame - result.Frame >= AvailableAfterFrames;
    }
}
