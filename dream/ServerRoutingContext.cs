public sealed class ServerRoutingContext
{
    public ServerRoutingContext(
        string connectionId,
        IAuthoritativeSessionHost? authority = null,
        IPlayerCommandInbox? commandInbox = null)
    {
        ConnectionId = connectionId;
        Authority = authority;
        CommandInbox = commandInbox;
    }

    public string ConnectionId { get; }

    public IAuthoritativeSessionHost? Authority { get; }

    public IPlayerCommandInbox? CommandInbox { get; }

    public ClientJoinRequestMessage? LastJoinRequest { get; set; }

    public PlayerCommandMessage? LastCommandMessage { get; set; }

    public PlayerActionMessage? LastActionMessage { get; set; }

    public List<IServerToClientMessage> OutboundMessages { get; } = [];

    public void ClearOutboundMessages()
    {
        OutboundMessages.Clear();
    }
}
