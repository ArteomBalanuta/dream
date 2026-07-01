using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Server;

public interface ITickDrivenAuthoritativeSessionHost : IAuthoritativeSessionHost
{
    void AdvanceTick(
        ulong serverTick,
        float deltaTime,
        IReadOnlyList<PlayerCommandFrame> commandFrames,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true);
}
