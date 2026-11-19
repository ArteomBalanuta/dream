using Dream.Simulation.Runtime;

namespace Dream.Simulation.Combat;

// The timeline of every MeleeArc swing, in authoritative ticks from the accepted cast: the wind-up,
// the active window in which the arc deals damage, then the recovery back to stance. The server
// resolves hits only inside the active window; the player's "cleave" clip and the slash effect are
// authored and timed from these same numbers, and tests pin both to them.
public static class MeleeArcSwing
{
    public const int WindupTicks = 20;
    public const int ActiveTicks = 10;
    public const int RecoveryTicks = 24;
    public const int TotalTicks = WindupTicks + ActiveTicks + RecoveryTicks;

    public const float WindupSeconds = WindupTicks / (float)SimulationClock.AuthoritativeTicksPerSecond;
    public const float ActiveSeconds = ActiveTicks / (float)SimulationClock.AuthoritativeTicksPerSecond;
    public const float ActiveEndSeconds = (WindupTicks + ActiveTicks) / (float)SimulationClock.AuthoritativeTicksPerSecond;
    public const float TotalSeconds = TotalTicks / (float)SimulationClock.AuthoritativeTicksPerSecond;
}
