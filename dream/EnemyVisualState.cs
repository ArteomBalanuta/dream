public sealed class EnemyVisualState
{
    public string? CurrentClipName { get; private set; }

    public void ApplyClip(AnimatedModel model, string clipName)
    {
        if (string.Equals(CurrentClipName, clipName, StringComparison.Ordinal))
        {
            return;
        }

        CurrentClipName = clipName;
        model.PlayLoop(clipName);
    }
}
