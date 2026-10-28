using Dream.Simulation.Players;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Protocol;

namespace Dream.Simulation.Runtime;

public interface ITickDrivenAuthoritativeSessionHost : IAuthoritativeSessionHost
{
    IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
        ulong serverTick,
        float deltaTime,
        IReadOnlyList<PlayerCommandFrame> commandFrames,
        IReadOnlyList<QueuedPlayerAction> queuedActions,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true);

    IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
        ulong serverTick,
        float deltaTime,
        IReadOnlyList<PlayerCommandFrame> commandFrames,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true)
    {
        return AdvanceTick(
            serverTick,
            deltaTime,
            commandFrames,
            Array.Empty<QueuedPlayerAction>(),
            playerTuning,
            allowGameplaySimulation);
    }
}
