using Dream.Simulation.Runtime;

namespace Dream.Simulation.Players;

// Wave I: how long a dead player waits. Ticks, never wall time, so the respawn tick is the same in every
// lockstep run. The client reads the same constant for its countdown.
public static class PlayerRespawnRules
{
    public const int DelayTicks = 5 * SimulationClock.AuthoritativeTicksPerSecond;

    // After a respawn, enemies neither target nor damage the player for this many ticks (3 s). Attacking ends it.
    public const int SpawnProtectionTicks = 3 * SimulationClock.AuthoritativeTicksPerSecond;

    // The longest a join's opt-in hold keeps protection without any input (5 minutes). The hold ends at the
    // cap even without input, and an input after the cap grants no window (an AFK player is not protected).
    public const int JoinHoldMaxTicks = 300 * SimulationClock.AuthoritativeTicksPerSecond;
}
