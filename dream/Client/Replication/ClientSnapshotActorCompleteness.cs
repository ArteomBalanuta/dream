using Dream.Simulation.World;

namespace Dream.Client.Replication;

// Tells the appliers that a snapshot's actor list is incomplete, so absence from it does not mean
// an actor left.
//
// ActorStateCache withholds every actor it cannot reconstitute (a broken delta chain, a delta for
// an actor it never saw whole) rather than show stale values as current - see its header. Everything
// downstream reads Snapshot.Actors as the full set of live actors, so "withheld" and "gone" were the
// same signal: one broken chain despawned every enemy, NPC and remote player on the client until the
// next keyframe arrived.
//
// Read from the snapshot itself, not from the cache's live StaleActorCount: snapshots are queued
// (a world transfer can hold them) and the cache has already moved on by the time the frame thread
// applies one. A delta frame's output holds exactly the actors its records named, minus the withheld
// ones, so a shortfall against the record count is the withholding. A keyframe never withholds.
public static class ClientSnapshotActorCompleteness
{
    public static bool WithholdsActors(in ServerSnapshotEnvelope snapshot) =>
        snapshot.ActorFrame is { IsKeyframe: false } frame &&
        frame.Records.Length > (snapshot.Actors?.Count ?? 0);
}
