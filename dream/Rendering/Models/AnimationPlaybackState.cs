namespace Dream.Rendering.Models;

public readonly record struct AnimationPlaybackState(
    string? ClipName,
    AnimationPlaybackMode Mode,
    float TimeSeconds,
    bool IsPlaying)
{
    public static AnimationPlaybackState Stopped => new(null, AnimationPlaybackMode.Loop, 0f, false);

    public AnimationPlaybackState Play(string clipName, AnimationPlaybackMode mode)
    {
        if (IsPlaying && ClipName == clipName && Mode == mode)
        {
            return this;
        }

        return new AnimationPlaybackState(clipName, mode, 0f, true);
    }

    public AnimationPlaybackState Advance(float deltaTime, float durationSeconds, out bool finished)
    {
        finished = false;

        if (!IsPlaying)
        {
            return this;
        }

        float nextTime = TimeSeconds + deltaTime;
        if (Mode == AnimationPlaybackMode.Loop)
        {
            float wrapped = durationSeconds <= 0f ? 0f : nextTime % durationSeconds;
            return this with { TimeSeconds = wrapped };
        }

        if (nextTime >= durationSeconds)
        {
            finished = true;
            return this with { TimeSeconds = durationSeconds, IsPlaying = false };
        }

        return this with { TimeSeconds = nextTime };
    }
}
