public sealed class GameplayInputResumeFilter
{
    private bool _wasGameplayInputAllowed = true;

    public PlayerCommandFrame Filter(PlayerCommandFrame frame, bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            _wasGameplayInputAllowed = false;
            return frame;
        }

        if (_wasGameplayInputAllowed)
        {
            return frame;
        }

        _wasGameplayInputAllowed = true;
        return frame with { LookIntent = PlayerLookIntent.None };
    }
}
