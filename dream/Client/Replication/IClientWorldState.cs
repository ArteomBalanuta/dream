using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

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

    void RebuildMapForFloorSize(int floorSize);

    bool TryResolveCombatActor(int actorId, out CombatActorHandle actor);
}
