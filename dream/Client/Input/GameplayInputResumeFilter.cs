using Dream.Simulation.Players;

namespace Dream.Client.Input;

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
