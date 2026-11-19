using Dream.Simulation.Runtime;

namespace Dream.Simulation.Combat;

// The timeline of every MeleeArc swing, in authoritative ticks from the accepted cast: a short wind-up, the
// active window in which the arc deals damage, then the recovery back to stance. It is timed as a cut, not a
// draw: the wind-up is a brief anticipation, the active window one fast sweep that still spans several ticks so
// a target stepping into the cone mid-swing is resolved, and the recovery is the follow-through. ActiveTicks is
// even so the clip's mid-window key falls on a whole tick. The server resolves hits only inside the active
// window; the player's "cleave" clip, the blade trail and the slash effect are authored and timed from these same
// numbers, and tests pin them all to them.
public static class MeleeArcSwing
{
    public const int WindupTicks = 8;
    public const int ActiveTicks = 6;
    public const int RecoveryTicks = 16;
    public const int TotalTicks = WindupTicks + ActiveTicks + RecoveryTicks;

    public const float WindupSeconds = WindupTicks / (float)SimulationClock.AuthoritativeTicksPerSecond;
    public const float ActiveSeconds = ActiveTicks / (float)SimulationClock.AuthoritativeTicksPerSecond;
    public const float ActiveEndSeconds = (WindupTicks + ActiveTicks) / (float)SimulationClock.AuthoritativeTicksPerSecond;
    public const float TotalSeconds = TotalTicks / (float)SimulationClock.AuthoritativeTicksPerSecond;
}
