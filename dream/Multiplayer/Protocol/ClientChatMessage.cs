namespace Dream.Multiplayer.Protocol;

// M1 (protocol v14). A chat line from a joined client. Target names the recipient of a Whisper and
// is null for every other channel. The server decides the sender name; the client never sends one.
public sealed record ClientChatMessage(ChatChannel Channel, string Text, string? Target = null) : IClientToServerMessage
{
    public const string Kind = "client.chat";

    // Longest accepted Text, in characters, after the server strips control and bidi characters.
    public const int MaxTextLength = 256;

    // Longest accepted Target, in characters.
    public const int MaxTargetLength = 64;

    public string MessageKind => Kind;
}
