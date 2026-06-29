public sealed record ServerActionMessage(AuthoritativeActionResult Result) : IServerToClientMessage
{
    public string MessageKind => "server.action";
}
