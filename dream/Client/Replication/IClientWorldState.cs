using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;

namespace Dream.Client.Replication;

// What the client replication appliers need from a world: the replicated map, the local
// player, replicated mode, combat-actor lookup for feedback and the world-mutation baseline.
// ClientWorldSession is its only implementation. The authoritative WorldSessionRuntime does not
// implement it, so the server simulation carries no client replication-ack state.
public interface IClientWorldState : IReplicatedSessionStateTarget
{
    Map Map { get; }

    Player Player { get; }

    PlayerInteractionMode InteractionMode { get; }

    ulong LastAppliedReplicatedWorldMutationSequence { get; }

    void AcknowledgeReplicatedWorldMutations(ulong sequence);

    // The world this client shows: the join-accepted descriptor, or a floor-size default before one arrives.
    WorldDescriptor World { get; }

    // Rebuilds the map at the descriptor's size and records it as the current world.
    // A non-null source builds the map from it (stored worlds); null builds the generated map.
    void RebuildMapForWorld(WorldDescriptor world, IMapSource? source = null);

    // Records the descriptor when the map already has its size.
    void AdoptWorldDescriptor(WorldDescriptor world);

    bool TryResolveCombatActor(int actorId, out CombatActorHandle actor);
}
