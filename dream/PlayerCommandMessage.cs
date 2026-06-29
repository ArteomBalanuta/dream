public sealed record PlayerCommandMessage(PlayerCommandFrame CommandFrame) : IClientToServerMessage
{
    public string MessageKind => "player.command";
}
