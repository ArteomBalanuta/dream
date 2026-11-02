using Dream.Simulation.Players;

namespace Dream.Client.Input;

public sealed class PlayerCommandOneShotInputFilter
{
    private bool _wasJumpPressed;
    private bool _wasFreezePressed;
    private bool _wasToggleNoclipPressed;

    // False against a server that drops the noclip and freeze toggles (anything but the integrated
    // host): predicting them there would move the player until each snapshot rubber-bands it back.
    public bool AllowDevToggles { get; set; } = true;

    public PlayerCommandFrame Filter(PlayerCommandFrame frame)
    {
        PlayerMoveIntent move = frame.MoveIntent;
        PlayerMoveIntent filteredMove = new(
            move.ForwardAxis,
            move.StrafeAxis,
            move.VerticalAxis,
            JumpPressed: move.JumpPressed && !_wasJumpPressed,
            FreezePressed: AllowDevToggles && move.FreezePressed && !_wasFreezePressed,
            ToggleNoclipPressed: AllowDevToggles && move.ToggleNoclipPressed && !_wasToggleNoclipPressed);

        _wasJumpPressed = move.JumpPressed;
        _wasFreezePressed = move.FreezePressed;
        _wasToggleNoclipPressed = move.ToggleNoclipPressed;

        return frame with { MoveIntent = filteredMove };
    }
}
