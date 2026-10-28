using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// Wave 7 tester (GPU-5): frames where no section runs (a loading screen, a menu frame, a frame
// whose world pass threw before BeginSection) must still resolve, report zero section times,
// and must not leak a previous frame's section result into a later frame that reuses the slot.
public class Wave7GpuFrameTimerSectionlessFrameTests
{
    [Fact]
    public void FrameWithoutAnySection_ResolvesWithZeroSectionTimes_AndItsPrimitives()
    {
        ImmediateQueryApi api = new();
        using GpuFrameTimer timer = new(api);

        api.NextPrimitives = 123;
        timer.BeginFrame();
        timer.EndFrame();
        for (int i = 0; i < GpuFrameTimer.MinReadLatencyFrames; i++)
        {
            timer.BeginFrame();
            timer.EndFrame();
        }

        Assert.Equal(1, timer.Latest.FrameNumber);
        Assert.Equal(0d, timer.Latest.WorldMilliseconds);
        Assert.Equal(0d, timer.Latest.ActorsMilliseconds);
        Assert.Equal(0d, timer.Latest.HudMilliseconds);
        Assert.Equal(123, timer.Latest.PrimitivesGenerated);
        Assert.Equal(0, timer.MissedFrames);
        Assert.Equal(0, api.TimeElapsedBegins);
        Assert.Equal(0, api.ResultReadsOfUnissuedTimeQueries);
    }

    [Fact]
    public void SectionlessFrame_ReusingASlotThatOnceTimedTheWorld_ReportsNoStaleWorldTime()
    {
        ImmediateQueryApi api = new();
        using GpuFrameTimer timer = new(api);
        int ring = timer.RingSize;

        // Frame 1 times the world; frame 1 + ring reuses its slot without any section.
        for (int frame = 1; frame <= ring + 1 + GpuFrameTimer.MinReadLatencyFrames; frame++)
        {
            timer.BeginFrame();
            if (frame == 1)
            {
                timer.BeginSection(GpuTimerSection.World);
                api.NextTimeElapsed = 5_000_000;
                timer.EndSection();
            }

            timer.EndFrame();
            if (timer.Latest.FrameNumber == 1)
            {
                Assert.Equal(5d, timer.Latest.WorldMilliseconds, 6);
            }

            if (timer.Latest.FrameNumber == ring + 1)
            {
                Assert.Equal(0d, timer.Latest.WorldMilliseconds);
            }
        }

        Assert.True(timer.Latest.FrameNumber >= ring + 1, $"latest {timer.Latest.FrameNumber}");
        Assert.Equal(0d, timer.Latest.WorldMilliseconds);
        Assert.Equal(0, timer.MissedFrames);
    }

    [Fact]
    public void BeginFrameWithoutEndFrame_ClosesThePreviousFrame_AndNeverNestsQueries()
    {
        ImmediateQueryApi api = new();
        using GpuFrameTimer timer = new(api);
        for (int frame = 0; frame < 12; frame++)
        {
            timer.BeginFrame();
            if (frame % 3 == 0)
            {
                timer.BeginSection(GpuTimerSection.Hud);
            }

            // No EndSection and no EndFrame: the next BeginFrame must close both.
        }

        Assert.Equal(0, api.NestedBegins);
        Assert.True(timer.Latest.HasValue);
        Assert.Equal(0, timer.MissedFrames);
    }

    // Every query result is available as soon as its query ends.
    private sealed class ImmediateQueryApi : IGpuQueryApi
    {
        private readonly Dictionary<int, long> _results = new();
        private readonly HashSet<int> _timeQueries = [];
        private readonly HashSet<int> _issuedTimeQueries = [];
        private int _next = 1;
        private int? _activeTime;
        private int? _activePrimitives;

        public long NextTimeElapsed { get; set; }

        public long NextPrimitives { get; set; }

        public int TimeElapsedBegins { get; private set; }

        public int NestedBegins { get; private set; }

        public int ResultReadsOfUnissuedTimeQueries { get; private set; }

        public int CreateQuery() => _next++;

        public void DeleteQuery(int query)
        {
        }

        public void BeginTimeElapsed(int query)
        {
            if (_activeTime != null)
            {
                NestedBegins++;
            }

            TimeElapsedBegins++;
            _activeTime = query;
            _timeQueries.Add(query);
            _issuedTimeQueries.Add(query);
            _results.Remove(query);
        }

        public void EndTimeElapsed()
        {
            _results[_activeTime!.Value] = NextTimeElapsed;
            NextTimeElapsed = 0;
            _activeTime = null;
        }

        public void BeginPrimitivesGenerated(int query)
        {
            if (_activePrimitives != null)
            {
                NestedBegins++;
            }

            _activePrimitives = query;
            _results.Remove(query);
        }

        public void EndPrimitivesGenerated()
        {
            _results[_activePrimitives!.Value] = NextPrimitives;
            _activePrimitives = null;
        }

        public bool IsResultAvailable(int query) => _results.ContainsKey(query);

        public long GetResult(int query)
        {
            if (!_issuedTimeQueries.Contains(query) && !_results.ContainsKey(query))
            {
                ResultReadsOfUnissuedTimeQueries++;
            }

            return _results.TryGetValue(query, out long value) ? value : 0;
        }
    }
}
