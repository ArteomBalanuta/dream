namespace Dream.Multiplayer.Protocol;

public sealed class ProtocolRouter : IProtocolRouter
{
    private readonly IReadOnlyDictionary<string, IServerMessageHandler> _serverHandlers;
    private readonly IReadOnlyDictionary<string, IClientMessageHandler> _clientHandlers;

    public ProtocolRouter(
        IEnumerable<IServerMessageHandler> serverHandlers,
        IEnumerable<IClientMessageHandler> clientHandlers)
    {
        _serverHandlers = serverHandlers.ToDictionary(handler => handler.MessageKind, StringComparer.Ordinal);
        _clientHandlers = clientHandlers.ToDictionary(handler => handler.MessageKind, StringComparer.Ordinal);
    }

    public void RouteServerInbound(ServerRoutingContext context, IClientToServerMessage message)
    {
        if (!_serverHandlers.TryGetValue(message.MessageKind, out IServerMessageHandler? handler))
        {
            throw new InvalidOperationException($"No server handler registered for '{message.MessageKind}'.");
        }

        handler.Handle(context, message);
    }

    public void RouteClientInbound(ClientRoutingContext context, IServerToClientMessage message)
    {
        if (!_clientHandlers.TryGetValue(message.MessageKind, out IClientMessageHandler? handler))
        {
            throw new InvalidOperationException($"No client handler registered for '{message.MessageKind}'.");
        }

        handler.Handle(context, message);
    }
}
