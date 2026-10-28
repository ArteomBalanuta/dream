using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;


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

    // Playback state lives in the per-actor AnimatedModelInstance (GPU-4); the shared
    // AnimatedModel holds none.
    [Fact]
    public void StopAnimation_ClearsIdleFallbackState()
    {
        AnimatedModelInstance instance = CreateInstanceForPlaybackTests();
        SetField(instance, "_idleClipName", "Idle");
        SetField(instance, "_playback", new AnimationPlaybackState("Use", AnimationPlaybackMode.OneShot, 0f, true));

        instance.StopAnimation();

        Assert.Equal(AnimationPlaybackState.Stopped, GetField<AnimationPlaybackState>(instance, "_playback"));
        Assert.Null(GetField<string?>(instance, "_idleClipName"));
    }

    [Fact]
    public void Update_InvalidClip_ClearsPlaybackState()
    {
        AnimatedModelInstance instance = CreateInstanceForPlaybackTests();
        SetField(instance, "_playback", new AnimationPlaybackState("Missing", AnimationPlaybackMode.OneShot, 0f, true));

        bool finished = instance.Update(0.1f);

        Assert.False(finished);
        Assert.Equal(AnimationPlaybackState.Stopped, GetField<AnimationPlaybackState>(instance, "_playback"));
    }

    [Fact]
    public void AnimatedModel_Holds_No_Playback_State()
    {
        string[] fields = typeof(AnimatedModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.Name)
            .ToArray();

        Assert.DoesNotContain("_playback", fields);
        Assert.DoesNotContain("_idleClipName", fields);
        Assert.Null(typeof(AnimatedModel).GetMethod("Update"));
        Assert.Null(typeof(AnimatedModel).GetMethod("PlayLoop"));
        Assert.Null(typeof(AnimatedModel).GetMethod("PlayOneShot"));
    }

    // A one-node glTF with no clips registered: every clip name is missing.
    private static AnimatedModelInstance CreateInstanceForPlaybackTests()
    {
        ModelRoot root = ModelRoot.CreateModel();
        root.UseScene("scene").CreateNode("node");
        return new AnimatedModelInstance(new AnimatedModelSkeleton(root, new Dictionary<string, Animation>(), skinJoints: null));
    }

    private static T GetField<T>(AnimatedModelInstance instance, string fieldName)
    {
        FieldInfo field = typeof(AnimatedModelInstance).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(instance)!;
    }

    private static void SetField<T>(AnimatedModelInstance instance, string fieldName, T value)
    {
        FieldInfo field = typeof(AnimatedModelInstance).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }
}
