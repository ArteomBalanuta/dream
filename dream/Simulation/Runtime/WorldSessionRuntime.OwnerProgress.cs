using Dream.Multiplayer.Sessions;

namespace Dream.Simulation.Runtime;

// Owner-only progress and the character presence lease (M8 workstream C). Contract stubs that do
// nothing until workstream C fills this file in.
public sealed partial class WorldSessionRuntime
{
    // On the vitals cadence, queues ServerPlayerProgress for the sessions whose values changed.
    private void StepOwnerProgress(ulong tick)
    {
    }

    private void ForgetOwnerProgress(PlayerSessionId session)
    {
    }

    // Every 960 ticks, renews the lease of the characters in this world.
    private void StepPresence(ulong tick)
    {
    }

    private void ReleasePresenceOnDetach(string characterKey)
    {
    }
}
