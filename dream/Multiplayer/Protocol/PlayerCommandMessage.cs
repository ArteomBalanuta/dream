using Dream.Simulation.Players;

namespace Dream.Multiplayer.Protocol;

public sealed record PlayerCommandMessage(PlayerCommandFrame CommandFrame) : IClientToServerMessage
{
    public string MessageKind => "player.command";
}
