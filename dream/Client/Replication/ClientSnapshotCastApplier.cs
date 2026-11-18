using Dream.Rendering.Effects;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

// Turns the cast events in a snapshot into SkillCastEvents for the FX bridge, so a peer's cast draws here.
// Presentation only, and it publishes the same event the local player's own cast publishes
// (ClientSkillCastPublisher) rather than calling FX: FxEventBridge stays the only consumer.
// Runs on the frame thread, so nothing here throws: a bad event is skipped. Plain for loop, no LINQ,
// no closures; the apply path is held to 0 bytes by ApplierAllocationTests.
public static class ClientSnapshotCastApplier
{
    // A merged backlog or a hostile server cannot flood the FX pool from one frame.
    public const int MaxCastEventsPerSnapshot = 64;

    // localServerActorId is the server's actor id for the local player, 0 when not yet known (suppress
    // nothing). The caster already drew its own cast from the unicast AbilityUsedResult, and the shared
    // broadcast body cannot omit it per recipient, so an event naming the local actor is skipped here.
    // Returns the number of events published. Absent (null) and empty both mean no casts.
    public static int Apply(SceneEventHub events, ServerSnapshotEnvelope snapshot, int localServerActorId)
    {
        if (events == null || snapshot.CastEvents is not { } casts)
        {
            return 0;
        }

        int published = 0;
        int count = Math.Min(casts.Count, MaxCastEventsPerSnapshot);
        for (int i = 0; i < count; i++)
        {
            CastEventDelta cast = casts[i];
            if (localServerActorId != 0 && cast.ActorId == localServerActorId)
            {
                continue;
            }

            if (ClientSkillCastPublisher.Publish(events, cast.Kind, cast.Origin, cast.Direction, cast.Range))
            {
                published++;
            }
        }

        return published;
    }
}
