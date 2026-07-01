using Dream.Simulation.Players;

namespace Dream.Client.Replication;

public interface IReplicatedSessionStateTarget
{
    void SetAuthoritativeInteractionMode(PlayerInteractionMode interactionMode);
}
