public sealed class ClientProtocolSession
{
    public ClientProtocolSession(IProtocolRouter router)
    {
        Router = router;
    }

    public ClientRoutingContext Context { get; } = new();

    public IProtocolRouter Router { get; }

    public static ClientProtocolSession CreateDefault()
    {
        return new ClientProtocolSession(new ProtocolRouter(
            Array.Empty<IServerMessageHandler>(),
            [
                new ServerHelloMessageHandler(),
                new JoinAcceptedMessageHandler(),
                new JoinRejectedMessageHandler(),
                new ServerSnapshotMessageHandler(),
                new ServerActionMessageHandler()
            ]));
    }

    public void ApplyServerMessage(IServerToClientMessage message)
    {
        Router.RouteClientInbound(Context, message);
    }
}
