namespace Dream.Multiplayer.Protocol;

// M1 (protocol v14). One chat line delivered to a client. From is the sender's display name as the
// server knows it ("" for a System line).
public sealed record ServerChatMessage(ChatChannel Channel, string From, string Text, ulong ServerTick) : IServerToClientMessage
{
    public const string Kind = "server.chat";

    public string MessageKind => Kind;
}
