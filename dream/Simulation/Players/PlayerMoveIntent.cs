namespace Dream.Simulation.Players;

public readonly record struct PlayerMoveIntent(
    float ForwardAxis,
    float StrafeAxis,
    float VerticalAxis = 0f,
    bool JumpPressed = false,
    bool FreezePressed = false,
    bool ToggleNoclipPressed = false,
    // Held, not a one-shot press: the sprint tier is on for every tick the key is down. Trailing
    // so every existing positional construction keeps its meaning.
    bool SprintHeld = false)
{
    public static PlayerMoveIntent None => new(0f, 0f, 0f, false, false, false);
}
