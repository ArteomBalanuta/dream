using System.Text.Json.Serialization;
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
    // Equality: ActorDelta and ActorFrame below take part in the record's generated Equals and
    // GetHashCode (ActorDelta by value, which compares its base list by reference; ActorFrame by
    // reference), like the list members above, which also compare by reference. Two envelopes
    // with the same content but a different wire framing (a decoded copy, a keyframe twin, a
    // rebased merged frame) are therefore not equal; compare fields when content matters.
    //
    // SCALE-2. Set by the runtime on a broadcast export: the encoder writes the actors as a delta
    // frame against the previous export (or as a keyframe) instead of the full list. Actors stays
    // the full list; nothing in memory sees the deltas. Never on the wire as itself.
    [JsonIgnore]
    public ActorDeltaContext? ActorDelta { get; init; }

    // SCALE-2. Set by the decoder on a snapshot whose actors came as a frame: the records exactly
    // as received, so re-encoding writes them back unchanged. Actors is null until the client's
    // ActorStateCache reconstitutes them (a keyframe decodes with Actors already filled in).
    [JsonIgnore]
    public ActorWireFrame? ActorFrame { get; init; }

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
