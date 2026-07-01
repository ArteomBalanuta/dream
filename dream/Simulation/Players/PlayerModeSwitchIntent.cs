namespace Dream.Simulation.Players;

public readonly record struct PlayerModeSwitchIntent(PlayerInteractionMode? Mode)
{
    public static PlayerModeSwitchIntent None => new(null);
}
