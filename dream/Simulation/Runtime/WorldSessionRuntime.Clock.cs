using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

public sealed partial class WorldSessionRuntime
{
    public WorldClock WorldClock { get; private set; } = new(WorldClockSettings.Default);
}
