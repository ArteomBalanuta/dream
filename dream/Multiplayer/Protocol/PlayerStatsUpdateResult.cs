using Dream.Simulation.Players;

namespace Dream.Multiplayer.Protocol;

public sealed record PlayerStatsUpdateResult(PlayerCombatStatsSnapshot Stats) : IActionResultPayload;
