namespace Dream.Multiplayer.Protocol;

public sealed record ClientJoinRequestMessage(string RequestedSessionId, string DisplayName) : IClientToServerMessage
{
    public string MessageKind => "client.join-request";
}
