public sealed record JoinRejectedMessage(string Reason) : IServerToClientMessage
{
    public string MessageKind => "server.join-rejected";
}
