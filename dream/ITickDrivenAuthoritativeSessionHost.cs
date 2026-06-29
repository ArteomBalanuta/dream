public interface ITickDrivenAuthoritativeSessionHost : IAuthoritativeSessionHost
{
    void AdvanceTick(
        ulong serverTick,
        float deltaTime,
        IReadOnlyList<PlayerCommandFrame> commandFrames,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true);
}
