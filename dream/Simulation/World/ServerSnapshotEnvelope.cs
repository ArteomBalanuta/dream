using Dream.Simulation.Players;

namespace Dream.Simulation.World;

public readonly record struct ServerSnapshotEnvelope(
    ulong ServerTick,
    IReadOnlyDictionary<string, ulong> LastAcceptedCommandSequences,
    IReadOnlyList<ActorSnapshot> Actors,
    PlayerInteractionMode InteractionMode,
    WorldSnapshot? World,
    IReadOnlyList<WorldMutationDelta> WorldMutations,
    IReadOnlyList<DamageEventDelta> DamageEvents,
    // WorldStateChecksum after every mutation up to WorldChecksumSequence; sent periodically.
    ulong? WorldChecksum = null,
    ulong WorldChecksumSequence = 0)
{
    // Merges two consecutive unsent snapshots for one connection: the newer actor state, tick
    // and interaction mode win, accepted command sequences take the maximum, and world mutations
    // and damage events from both are kept in order so no world change is lost. Snapshots that
    // carry a full world are not merged, because their mutations are already reflected in it.
    public static bool TryCoalesce(
        ServerSnapshotEnvelope older,
        ServerSnapshotEnvelope newer,
        out ServerSnapshotEnvelope merged)
    {
        if (older.World != null || newer.World != null)
        {
            merged = newer;
            return false;
        }

        Dictionary<string, ulong> sequences = new(older.LastAcceptedCommandSequences, StringComparer.Ordinal);
        foreach ((string sessionId, ulong sequence) in newer.LastAcceptedCommandSequences)
        {
            sequences[sessionId] = sequences.TryGetValue(sessionId, out ulong existing)
                ? Math.Max(existing, sequence)
                : sequence;
        }

        merged = newer with
        {
            LastAcceptedCommandSequences = sequences,
            WorldMutations = [.. older.WorldMutations, .. newer.WorldMutations],
            DamageEvents = [.. older.DamageEvents, .. newer.DamageEvents]
        };
        return true;
    }
}
