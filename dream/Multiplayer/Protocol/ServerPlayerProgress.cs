using Dream.Simulation.Players;

namespace Dream.Multiplayer.Protocol;

// M8 (protocol v18). Owner only, unprompted: the owner's level, experience and base stats, which actor
// snapshots stop carrying for other players' eyes.
public sealed record ServerPlayerProgress(int Level, int Experience, int ExperienceToNextLevel, PlayerCombatStatsSnapshot BaseStats) : IServerToClientMessage
{
    public const string Kind = "server.progress";
    public string MessageKind => Kind;
}
