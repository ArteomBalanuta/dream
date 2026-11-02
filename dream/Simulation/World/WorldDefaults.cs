namespace Dream.Simulation.World;

public static class WorldDefaults
{
    public const int StartupFloorSize = 56;

    // The floor size of a world the dedicated server creates when none exists yet (M9). The desktop and
    // test defaults stay at StartupFloorSize.
    public const int ServerNewWorldSize = 100;
}
