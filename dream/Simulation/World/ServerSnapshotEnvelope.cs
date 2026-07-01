using Dream.Simulation.Players;

namespace Dream.Simulation.World;

public readonly record struct ServerSnapshotEnvelope(
    ulong ServerTick,
    IReadOnlyDictionary<string, ulong> LastAcceptedCommandSequences,
    IReadOnlyList<ActorSnapshot> Actors,
    PlayerInteractionMode InteractionMode,
    WorldSnapshot? World,
    IReadOnlyList<WorldMutationDelta> WorldMutations,
    IReadOnlyList<DamageEventDelta> DamageEvents);
