using Dream.Simulation.Players;

namespace Dream.Application;


public static class GameSettingsSimulationExtensions
{
    public static PlayerSimulationTuning ToPlayerSimulationTuning(this GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PlayerSimulationTuning(
            settings.EyeHeightOffset,
            settings.MouseSensitivity,
            settings.MoveSpeed,
            settings.NoclipSpeed,
            settings.JumpVelocity);
    }
}
