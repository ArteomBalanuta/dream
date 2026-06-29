public sealed record ServerHelloMessage(string ServerName, int ProtocolVersion, ulong CurrentServerTick) : IServerToClientMessage
{
    public string MessageKind => "server.hello";
}
