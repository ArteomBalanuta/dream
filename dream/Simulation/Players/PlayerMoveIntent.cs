namespace Dream.Simulation.Players;

public readonly record struct PlayerMoveIntent(
    float ForwardAxis,
    float StrafeAxis,
    float VerticalAxis = 0f,
    bool JumpPressed = false,
    bool FreezePressed = false,
    bool ToggleNoclipPressed = false)
{
    public static PlayerMoveIntent None => new(0f, 0f, 0f, false, false, false);
}
