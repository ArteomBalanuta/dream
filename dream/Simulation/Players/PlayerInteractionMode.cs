namespace Dream.Simulation.Players;

public enum PlayerInteractionMode
{
    Building,
    Combat
}

public static class PlayerInteractionModeExtensions
{
    public static string ToHudLabel(this PlayerInteractionMode mode)
    {
        return mode switch
        {
            PlayerInteractionMode.Building => "Building",
            PlayerInteractionMode.Combat => "Combat",
            _ => mode.ToString()
        };
    }
}
