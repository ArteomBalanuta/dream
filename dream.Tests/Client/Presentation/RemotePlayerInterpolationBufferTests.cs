using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;

namespace Dream.Tests.Client.Presentation;

public class RemotePlayerInterpolationBufferTests
{
    [Fact]
    public void Sample_InterpolatesBetweenAuthoritativeRemoteSnapshots()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(1, new Vector3(0f, 0f, 0f)));
        buffer.AddSample(CreateSample(2, new Vector3(10f, 0f, 0f)));

        Assert.True(buffer.TrySample(1.5, out InterpolatedActorPose pose));
        Assert.Equal(5f, pose.Position.X, 4);
        Assert.Equal(0f, pose.Position.Y, 4);
        Assert.Equal(0f, pose.Position.Z, 4);
    }

    [Fact]
    public void TrySample_EmptyBuffer_ReturnsFalse()
    {
        RemotePlayerInterpolationBuffer buffer = new();

        Assert.False(buffer.TrySample(10d, out _));
    }

    [Fact]
    public void AddSample_OutOfOrderTicks_AreSortedByServerTick()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(12, new Vector3(20f, 0f, 0f)));
        buffer.AddSample(CreateSample(10, new Vector3(0f, 0f, 0f)));
        buffer.AddSample(CreateSample(11, new Vector3(10f, 0f, 0f)));

        Assert.Equal(10UL, buffer.OldestTick);
        Assert.Equal(12UL, buffer.NewestTick);
        Assert.True(buffer.TrySample(10.5, out InterpolatedActorPose early));
        Assert.Equal(5f, early.Position.X, 4);
        Assert.True(buffer.TrySample(11.25, out InterpolatedActorPose late));
        Assert.Equal(12.5f, late.Position.X, 4);
    }

    [Fact]
    public void AddSample_SameFrameBurst_InterpolatesAcrossEachTick()
    {
        // Regression: snapshots drained in one frame used to share a receive time, so the
        // interpolation span was zero and remote players snapped to the newest sample.
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(30, new Vector3(0f, 0f, 0f)));
        buffer.AddSample(CreateSample(31, new Vector3(1f, 0f, 0f)));
        buffer.AddSample(CreateSample(32, new Vector3(3f, 0f, 0f)));

        Assert.True(buffer.TrySample(30.5, out InterpolatedActorPose first));
        Assert.Equal(0.5f, first.Position.X, 4);
        Assert.True(buffer.TrySample(31.5, out InterpolatedActorPose second));
        Assert.Equal(2f, second.Position.X, 4);
    }

    [Fact]
    public void TrySample_CoalescedTickGap_InterpolatesOverRealTickSpan()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(10, new Vector3(0f, 0f, 0f)));
        buffer.AddSample(CreateSample(14, new Vector3(8f, 0f, 4f)));

        Assert.True(buffer.TrySample(12d, out InterpolatedActorPose pose));
        Assert.Equal(4f, pose.Position.X, 4);
        Assert.Equal(2f, pose.Position.Z, 4);
    }

    [Fact]
    public void AddSample_EqualTick_LatestSampleWins()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(5, new Vector3(1f, 0f, 0f)));
        buffer.AddSample(CreateSample(5, new Vector3(2f, 0f, 0f)));

        Assert.Equal(1, buffer.Count);
        Assert.True(buffer.TrySample(5d, out InterpolatedActorPose pose));
        Assert.Equal(2f, pose.Position.X, 4);
    }

    [Fact]
    public void TrySample_PastNewest_HoldsNewestSample()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(10, new Vector3(0f, 0f, 0f)));
        buffer.AddSample(CreateSample(11, new Vector3(1f, 0f, 0f), Vector3.UnitX));

        Assert.True(buffer.TrySample(50d, out InterpolatedActorPose pose));
        Assert.Equal(1f, pose.Position.X, 4);
        Assert.Equal(Vector3.UnitX, pose.Facing);
    }

    [Fact]
    public void TrySample_BeforeOldest_HoldsOldestSample()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(10, new Vector3(3f, 0f, 0f)));
        buffer.AddSample(CreateSample(11, new Vector3(4f, 0f, 0f)));

        Assert.True(buffer.TrySample(-5d, out InterpolatedActorPose pose));
        Assert.Equal(3f, pose.Position.X, 4);
    }

    [Fact]
    public void TrySample_InterpolatesFacingAndKeepsItNormalized()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(CreateSample(0, Vector3.Zero, Vector3.UnitX));
        buffer.AddSample(CreateSample(2, Vector3.Zero, Vector3.UnitZ));

        Assert.True(buffer.TrySample(1d, out InterpolatedActorPose pose));
        Assert.Equal(1f, pose.Facing.Length, 4);
        Assert.Equal(pose.Facing.X, pose.Facing.Z, 4);
    }

    [Fact]
    public void AddSample_PrunesSamplesOlderThanTickSpan()
    {
        RemotePlayerInterpolationBuffer buffer = new(maxSamples: 32, maxTickSpan: 10);
        for (ulong tick = 0; tick <= 30; tick += 2)
        {
            buffer.AddSample(CreateSample(tick, new Vector3(tick, 0f, 0f)));
        }

        Assert.Equal(20UL, buffer.OldestTick);
        Assert.Equal(30UL, buffer.NewestTick);

        buffer.AddSample(CreateSample(5, new Vector3(-1f, 0f, 0f)));
        Assert.Equal(20UL, buffer.OldestTick);
    }

    [Fact]
    public void AddSample_CapsSampleCount()
    {
        RemotePlayerInterpolationBuffer buffer = new(maxSamples: 4, maxTickSpan: 1000);
        for (ulong tick = 1; tick <= 10; tick++)
        {
            buffer.AddSample(CreateSample(tick, new Vector3(tick, 0f, 0f)));
        }

        Assert.Equal(4, buffer.Count);
        Assert.Equal(7UL, buffer.OldestTick);
        Assert.Equal(10UL, buffer.NewestTick);
    }

    private static RemotePlayerSnapshotSample CreateSample(ulong tick, Vector3 position)
    {
        return CreateSample(tick, position, Vector3.UnitZ);
    }

    private static RemotePlayerSnapshotSample CreateSample(ulong tick, Vector3 position, Vector3 facing)
    {
        return new RemotePlayerSnapshotSample(10, "remote-1", tick, position, facing);
    }
}
