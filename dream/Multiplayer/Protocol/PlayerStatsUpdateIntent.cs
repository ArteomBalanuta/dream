using Dream.Simulation.Players;

namespace Dream.Multiplayer.Protocol;

public sealed record PlayerStatsUpdateIntent(PlayerCombatStatsSnapshot Stats) : IPlayerActionPayload;
