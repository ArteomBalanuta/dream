using Dream.Simulation.World;

namespace Dream.Client.Replication;

// Names the actors a snapshot withheld, so absence from its actor list is not read as departure
// for those actors alone.
//
// ActorStateCache withholds every actor it cannot reconstitute (a broken delta chain, a delta for
// an actor it never saw whole) rather than show stale values as current - see its header.
// Everything downstream reads Snapshot.Actors as the full set of live actors, so "withheld" and
// "gone" were the same signal: one broken chain despawned every enemy, NPC and remote player on
// the client until the next keyframe arrived.
//
// The decision is per actor, not per snapshot. One snapshot can both withhold an actor and carry a
// real departure - a coalesced snapshot spanning a death and a respawn does exactly that, and the
// respawn only snaps into place if the dead actor is still pruned. So the withheld actors are named
// and treated as present but not updated; every other absent actor is gone, as before.
//
// Read from the snapshot itself, not from the cache's live StaleActorCount: snapshots are queued
// (a world transfer can hold them) and the cache has already moved on by the time the frame thread
// applies one. A delta frame's output holds exactly the actors its records named, minus the
// withheld ones, so the records the list does not carry are the withheld ones. A keyframe never
// withholds, and a snapshot outside the chain carries no frame at all.
public static class ClientSnapshotActorCompleteness
{
    // Fills destination with the ids the snapshot's frame named but its actor list does not carry.
    // Cleared first, and left empty for a complete snapshot, so a caller that merges it into its
    // observed set changes nothing in the common case.
    public static void CollectWithheldActorIds(in ServerSnapshotEnvelope snapshot, HashSet<int> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        if (snapshot.ActorFrame is not { IsKeyframe: false } frame || frame.Records.Length == 0)
        {
            return;
        }

        ActorWireRecord[] records = frame.Records;
        for (int i = 0; i < records.Length; i++)
        {
            destination.Add(records[i].ActorId);
        }

        IReadOnlyList<ActorSnapshot>? actors = snapshot.Actors;
        for (int i = 0; i < (actors?.Count ?? 0); i++)
        {
            destination.Remove(actors![i].ActorId);
        }
    }

    // Whether anything at all was withheld, for the one consumer that cannot act per actor:
    // ClientNpcCache rebuilds its whole list from the snapshot, so an incomplete one is skipped
    // and the NPCs keep the positions of the last complete one.
    public static bool WithholdsActors(in ServerSnapshotEnvelope snapshot) =>
        snapshot.ActorFrame is { IsKeyframe: false } frame &&
        frame.Records.Length > (snapshot.Actors?.Count ?? 0);
}
