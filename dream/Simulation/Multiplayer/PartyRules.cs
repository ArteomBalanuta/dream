using Dream.Simulation.Runtime;

namespace Dream.Simulation.Multiplayer;

// The numbers of parties (M5). Ticks are server ticks, never wall time.
public static class PartyRules
{
    public const int MaxMembers = 5;

    // A pending invite lapses after 60 s.
    public const int InviteTimeoutTicks = 60 * SimulationClock.AuthoritativeTicksPerSecond;

    // A party mate this close (metres, 3D) to a kill shares its experience.
    public const float ShareRangeMeters = 40f;

    // The refresh cadence of ServerPartyState while parties exist (health bars), in ticks.
    public const int RefreshIntervalTicks = 32;

    // A TargetName longer than this is refused before any lookup.
    public const int MaxTargetNameChars = 64;
}
