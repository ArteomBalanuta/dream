using Dream.Simulation.Runtime;

namespace Dream.Simulation.Players;

// Wave I: how long a dead player waits. Ticks, never wall time, so the respawn tick is the same in every
// lockstep run. The client reads the same constant for its countdown.
public static class PlayerRespawnRules
{
    public const int DelayTicks = 5 * SimulationClock.AuthoritativeTicksPerSecond;
}
