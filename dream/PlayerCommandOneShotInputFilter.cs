public sealed class PlayerCommandOneShotInputFilter
{
    private bool _wasJumpPressed;
    private bool _wasFreezePressed;
    private bool _wasToggleNoclipPressed;

    public PlayerCommandFrame Filter(PlayerCommandFrame frame)
    {
        PlayerMoveIntent move = frame.MoveIntent;
        PlayerMoveIntent filteredMove = new(
            move.ForwardAxis,
            move.StrafeAxis,
            move.VerticalAxis,
            JumpPressed: move.JumpPressed && !_wasJumpPressed,
            FreezePressed: move.FreezePressed && !_wasFreezePressed,
            ToggleNoclipPressed: move.ToggleNoclipPressed && !_wasToggleNoclipPressed);

        _wasJumpPressed = move.JumpPressed;
        _wasFreezePressed = move.FreezePressed;
        _wasToggleNoclipPressed = move.ToggleNoclipPressed;

        return frame with { MoveIntent = filteredMove };
    }
}
