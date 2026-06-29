public sealed class ClientHelloHandler : IServerMessageHandler
{
    public string MessageKind => "client.hello";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        context.OutboundMessages.Add(new ServerHelloMessage(
            "dream-phase4-server",
            TransportEnvelope.CurrentProtocolVersion,
            CurrentServerTick: 0));
    }
}
