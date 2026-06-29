public sealed record ClientHelloMessage(string ClientName) : IClientToServerMessage
{
    public string MessageKind => "client.hello";
}
