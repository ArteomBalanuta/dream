using Dream.Client.Scenes;
using Xunit;

namespace Dream.Tests.Client.Scenes;

public class WorldReadinessTests
{
    private const double Frame = 1.0 / 60.0;

    private static WorldReadiness JoinedWithSnapshot()
    {
        WorldReadiness r = new();
        r.ObserveJoinApplied();
        r.ObserveSnapshotApplied();
        return r;
    }

    [Fact]
    public void NotReadyUntilJoinSnapshotAndSettledFrames()
    {
        WorldReadiness r = new();
        for (int i = 0; i < 50; i++)
        {
            r.ObserveFrame(0, 0, Frame);
        }

        Assert.False(r.Ready);
        r.ObserveJoinApplied();
        for (int i = 0; i < 50; i++)
        {
            r.ObserveFrame(0, 0, Frame);
        }

        Assert.False(r.Ready);
        r.ObserveSnapshotApplied();
        Assert.True(r.Ready); // the frames had already settled
    }

    [Fact]
    public void ReadyAfterExactlyWarmupSettledFrames()
    {
        WorldReadiness r = JoinedWithSnapshot();
        for (int i = 0; i < WorldReadiness.WarmupFrames - 1; i++)
        {
            r.ObserveFrame(0, 0, Frame);
            Assert.False(r.Ready);
        }

        r.ObserveFrame(0, 0, Frame);
        Assert.True(r.Ready);
        Assert.False(r.TimedOut);
        Assert.Equal(1f, r.Progress01);
        Assert.Equal("The world awaits.", r.Stage);
    }

    [Fact]
    public void RebuiltMeshOrPendingModelResetsTheSettleCount()
    {
        WorldReadiness r = JoinedWithSnapshot();
        for (int i = 0; i < 9; i++)
        {
            r.ObserveFrame(0, 0, Frame);
        }

        r.ObserveFrame(0, 1, Frame);
        for (int i = 0; i < 9; i++)
        {
            r.ObserveFrame(0, 0, Frame);
        }

        r.ObserveFrame(2, 0, Frame);
        Assert.False(r.Ready);
        for (int i = 0; i < 9; i++)
        {
            r.ObserveFrame(0, 0, Frame);
            Assert.False(r.Ready);
        }

        r.ObserveFrame(0, 0, Frame);
        Assert.True(r.Ready);
    }

    [Fact]
    public void TimesOutTwentySecondsAfterTheJoinNotBefore()
    {
        WorldReadiness r = new();
        r.ObserveFrame(3, 0, 100.0); // before the join: no clock
        Assert.False(r.Ready);
        r.ObserveJoinApplied();
        r.ObserveFrame(3, 0, 19.0);
        Assert.False(r.Ready);
        r.ObserveFrame(3, 0, 2.0);
        Assert.True(r.Ready);
        Assert.True(r.TimedOut);
        Assert.Equal(1f, r.Progress01);
    }

    [Fact]
    public void ProgressNeverDecreasesAndIsOneAtReady()
    {
        WorldReadiness r = new();
        float last = r.Progress01;
        void Check()
        {
            Assert.True(r.Progress01 >= last);
            last = r.Progress01;
        }

        r.ObserveFrame(0, 0, Frame); Check();
        r.ObserveJoinApplied(); Check();
        r.ObserveFrame(8, 0, Frame); Check();
        r.ObserveFrame(4, 0, Frame); Check();
        r.ObserveFrame(6, 0, Frame); Check(); // pending grew; latched
        r.ObserveSnapshotApplied(); Check();
        r.ObserveFrame(0, 2, Frame); Check();
        for (int i = 0; i < 9; i++)
        {
            r.ObserveFrame(0, 0, Frame);
            Check();
            Assert.False(r.Ready);
            Assert.True(r.Progress01 < 1f);
        }

        r.ObserveFrame(0, 3, Frame); Check();
        for (int i = 0; i < WorldReadiness.WarmupFrames; i++)
        {
            r.ObserveFrame(0, 0, Frame);
            Check();
        }

        Assert.True(r.Ready);
        Assert.Equal(1f, r.Progress01);
    }

    [Fact]
    public void StageFollowsTheLoad()
    {
        WorldReadiness r = new();
        Assert.Equal("Receiving the world...", r.Stage);
        r.ObserveJoinApplied();
        Assert.Equal("Finding your footing...", r.Stage);
        r.ObserveFrame(5, 0, Frame);
        r.ObserveSnapshotApplied();
        Assert.Equal("Summoning the beasts...", r.Stage);
        r.ObserveFrame(0, 0, Frame);
        Assert.Equal("Kindling the fires...", r.Stage);
        for (int i = 0; i < WorldReadiness.WarmupFrames; i++)
        {
            r.ObserveFrame(0, 0, Frame);
        }

        Assert.Equal("The world awaits.", r.Stage);
    }
}
