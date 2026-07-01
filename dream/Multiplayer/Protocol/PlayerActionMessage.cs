using Dream.Multiplayer.Sessions;

namespace Dream.Multiplayer.Protocol;

public sealed record PlayerActionMessage(
    PlayerSessionId SessionId,
    ulong ActionSequence,
    IPlayerActionPayload Payload) : IClientToServerMessage
{
    public string MessageKind => "player.action";
}
