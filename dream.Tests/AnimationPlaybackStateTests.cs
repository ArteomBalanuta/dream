using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using SharpGLTF.Schema2;
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
    public void Play_SameClipAndMode_IsIdempotent()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Idle", AnimationPlaybackMode.Loop)
            .Advance(0.75f, 2f, out _);

        AnimationPlaybackState replayed = state.Play("Idle", AnimationPlaybackMode.Loop);

        Assert.Equal(state, replayed);
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

    [Fact]
    public void Advance_StoppedState_IsNoOp()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped;

        AnimationPlaybackState next = state.Advance(0.5f, 1f, out bool finished);

        Assert.False(finished);
        Assert.Equal(state, next);
    }

    [Fact]
    public void Advance_Loop_WrapsDuration()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Idle", AnimationPlaybackMode.Loop);

        state = state.Advance(2.5f, 2f, out bool finished);

        Assert.False(finished);
        Assert.True(state.IsPlaying);
        Assert.Equal(0.5f, state.TimeSeconds);
    }

    [Fact]
    public void Advance_LoopWithNonPositiveDuration_ClampsToZero()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Idle", AnimationPlaybackMode.Loop)
            .Advance(0.5f, 2f, out _);

        state = state.Advance(0.25f, 0f, out bool finished);

        Assert.False(finished);
        Assert.True(state.IsPlaying);
        Assert.Equal(0f, state.TimeSeconds);
    }

    [Fact]
    public void Advance_CompletedOneShot_FinishesOnlyOnce()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Use", AnimationPlaybackMode.OneShot);

        state = state.Advance(1.25f, 1f, out bool finished);
        AnimationPlaybackState next = state.Advance(0.1f, 1f, out bool finishedAgain);

        Assert.True(finished);
        Assert.False(state.IsPlaying);
        Assert.False(finishedAgain);
        Assert.Equal(state, next);
    }

    [Fact]
    public void StopAnimation_ClearsIdleFallbackState()
    {
        AnimatedModel model = CreateAnimatedModelForPlaybackTests();
        SetField(model, "_idleClipName", "Idle");
        SetField(model, "_playback", new AnimationPlaybackState("Use", AnimationPlaybackMode.OneShot, 0f, true));

        model.StopAnimation();

        Assert.Equal(AnimationPlaybackState.Stopped, GetField<AnimationPlaybackState>(model, "_playback"));
        Assert.Null(GetField<string?>(model, "_idleClipName"));
    }

    [Fact]
    public void Update_InvalidClip_ClearsPlaybackState()
    {
        AnimatedModel model = CreateAnimatedModelForPlaybackTests();
        SetField(model, "_playback", new AnimationPlaybackState("Missing", AnimationPlaybackMode.OneShot, 0f, true));

        bool finished = model.Update(0.1f);

        Assert.False(finished);
        Assert.Equal(AnimationPlaybackState.Stopped, GetField<AnimationPlaybackState>(model, "_playback"));
    }

    private static AnimatedModel CreateAnimatedModelForPlaybackTests()
    {
#pragma warning disable SYSLIB0050
        AnimatedModel model = (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
#pragma warning restore SYSLIB0050
        SetField(model, "_animations", new Dictionary<string, Animation>());
        return model;
    }

    private static T GetField<T>(AnimatedModel model, string fieldName)
    {
        FieldInfo field = typeof(AnimatedModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(model)!;
    }

    private static void SetField<T>(AnimatedModel model, string fieldName, T value)
    {
        FieldInfo field = typeof(AnimatedModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(model, value);
    }
}
