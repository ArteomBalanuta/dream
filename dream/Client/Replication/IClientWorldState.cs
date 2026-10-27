using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

// What the client replication appliers need from a world: the replicated map, the local
// player, replicated mode, combat-actor lookup for feedback and the world-mutation baseline.
// ClientWorldSession implements it for the desktop client; WorldSessionRuntime also satisfies
// it so applier tests can run against an authoritative runtime.
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
