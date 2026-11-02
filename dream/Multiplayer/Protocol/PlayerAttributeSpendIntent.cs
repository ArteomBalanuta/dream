using Dream.Simulation.Stats;

namespace Dream.Multiplayer.Protocol;

// Spends unspent attribute points on Strength, Agility, Vitality or Energy.
public sealed record PlayerAttributeSpendIntent(StatId Attribute, int Points = 1) : IPlayerActionPayload;
