using Dream.Simulation.Players;

namespace Dream.Client.Input;

// The cursor mode changes at the end of the Update that opens or closes a window (the OS moves or
// re-centres the cursor then), so the pointer delta of the next few frames is that jump, not a look.
// Look stays zero for Frames frames after gameplay input resumes; GameplayInputResumeFilter alone
// covers only the first frame. Everything but look passes through.
public sealed class CursorChangeLookHoldoff
{
    public const int Frames = 3;

    private int _remaining;

    public PlayerCommandFrame Filter(PlayerCommandFrame frame, bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            _remaining = Frames;
            return frame;
        }

        if (_remaining <= 0)
        {
            return frame;
        }

        _remaining--;
        return frame with { LookIntent = PlayerLookIntent.None };
    }
}
