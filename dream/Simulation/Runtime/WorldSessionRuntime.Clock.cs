using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// The world clock: advanced once per AdvanceAuthoritativeTick (never by queued-action processing),
// so WorldTicks - CurrentServerTick stays constant for the life of the runtime.
public sealed partial class WorldSessionRuntime
{
    // The WorldTicks the current clock was created with. Every AdvanceAuthoritativeTick moves the
    // clock off it, which is how ConfigureWorldClock tells that the runtime has ticked.
    private long _worldClockConfiguredTicks;

    public WorldClock WorldClock { get; private set; } = new(WorldClockSettings.Default);

    // Replaces the clock with one at worldTicks (the persisted world time). Only before the first
    // tick: a later call would move WorldTicks - CurrentServerTick, which clients anchor on at join,
    // so it throws once the runtime has advanced or processed a tick.
    public void ConfigureWorldClock(WorldClockSettings settings, long worldTicks)
    {
        if (CurrentServerTick != 0 || WorldClock.WorldTicks != _worldClockConfiguredTicks)
        {
            throw new InvalidOperationException(
                $"ConfigureWorldClock must be called before the first tick (the runtime is at server tick {CurrentServerTick}).");
        }

        WorldClock = new WorldClock(settings, worldTicks);
        _worldClockConfiguredTicks = worldTicks;
    }

    long IRuntimePersistenceHost.WorldTicks => WorldClock.WorldTicks;
}
