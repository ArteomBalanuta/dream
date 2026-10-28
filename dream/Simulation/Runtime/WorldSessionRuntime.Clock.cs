using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// The world clock: advanced once per AdvanceAuthoritativeTick (never by queued-action processing),
// so WorldTicks - CurrentServerTick stays constant for the life of the runtime.
public sealed partial class WorldSessionRuntime
{
    public WorldClock WorldClock { get; private set; } = new(WorldClockSettings.Default);

    // Replaces the clock with one at worldTicks (the persisted world time). Call before the first
    // tick: a later call moves WorldTicks - CurrentServerTick, which clients anchor on at join.
    public void ConfigureWorldClock(WorldClockSettings settings, long worldTicks)
    {
        WorldClock = new WorldClock(settings, worldTicks);
    }

    long IRuntimePersistenceHost.WorldTicks => WorldClock.WorldTicks;
}
