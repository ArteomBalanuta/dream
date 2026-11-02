using Dream.Simulation.Stats;

namespace Dream.Multiplayer.Protocol;

// Spend stat points on one of the player's own stats (Strength, Vitality or Attack Speed). Points is
// a count of points, not a stat value; the server checks it against the points the level has earned
// (PlayerStatRules). It names no player: the server applies it to the sending session's character.
public sealed record PlayerStatSpendIntent(StatId Stat, int Points) : IPlayerActionPayload;
