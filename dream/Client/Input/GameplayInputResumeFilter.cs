using Dream.Simulation.Players;

namespace Dream.Client.Input;

public sealed class GameplayInputResumeFilter
{
    private bool _wasGameplayInputAllowed = true;

    // Set when gameplay input resumes: a use held through the pause (an attack held through death) does not
    // count until the button has been released once, so it cannot end spawn protection on the first frame.
    private bool _primaryUseNeedsRelease;

    public PlayerCommandFrame Filter(PlayerCommandFrame frame, bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            _wasGameplayInputAllowed = false;
            return frame;
        }

        if (_wasGameplayInputAllowed)
        {
            if (_primaryUseNeedsRelease)
            {
                if (!frame.PrimaryUsePressed)
                {
                    _primaryUseNeedsRelease = false;
                }
                else
                {
                    return frame with { PrimaryUsePressed = false };
                }
            }

            return frame;
        }

        _wasGameplayInputAllowed = true;
        _primaryUseNeedsRelease = frame.PrimaryUsePressed;
        PlayerMoveIntent move = frame.MoveIntent;
        return frame with
        {
            LookIntent = PlayerLookIntent.None,
            MoveIntent = new PlayerMoveIntent(
                move.ForwardAxis,
                move.StrafeAxis,
                move.VerticalAxis,
                JumpPressed: false,
                FreezePressed: false,
                ToggleNoclipPressed: false),
            PrimaryUsePressed = false
        };
    }
}
