using Xunit;

public class AnimationPlaybackStateTests
{
    [Fact]
    public void PlayLoop_SetsLoopingClip()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped;

        state = state.Play("Idle", AnimationPlaybackMode.Loop);

        Assert.Equal("Idle", state.ClipName);
        Assert.Equal(AnimationPlaybackMode.Loop, state.Mode);
        Assert.True(state.IsPlaying);
    }

    [Fact]
    public void PlayOneShot_ResetsTime()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Idle", AnimationPlaybackMode.Loop)
            .Advance(0.75f, 2f, out _);

        state = state.Play("Use", AnimationPlaybackMode.OneShot);

        Assert.Equal("Use", state.ClipName);
        Assert.Equal(0f, state.TimeSeconds);
    }

    [Fact]
    public void Advance_ReportsFinishedForOneShot()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Use", AnimationPlaybackMode.OneShot);

        state = state.Advance(1.25f, 1f, out bool finished);

        Assert.True(finished);
        Assert.Equal(1f, state.TimeSeconds);
    }
}
