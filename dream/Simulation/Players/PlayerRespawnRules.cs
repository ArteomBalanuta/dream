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

    // A seat that re-attaches within this long of its last fresh attach gets no hold (30 s). It stops a
    // client from re-arming the hold mid-fight by reconnecting, while a real reconnect after a crash or a
    // dropped connection (a relaunch takes longer) still gets it. A disconnected player is not a target
    // anyway, so the short window costs little.
    public const int JoinHoldReattachCooldownTicks = 30 * SimulationClock.AuthoritativeTicksPerSecond;
}
