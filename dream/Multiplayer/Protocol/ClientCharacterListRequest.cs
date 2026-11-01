using System.Text;

namespace Dream.Multiplayer.Protocol;

// M1 (protocol v14). Asks for the characters of the account the token belongs to, before a join.
// AccountToken is the player's secret (see ClientJoinRequestMessage): the server resolves it
// immediately and must never queue, store or log it. ToString prints nothing from it.
public sealed record ClientCharacterListRequest(string AccountToken) : IClientToServerMessage
{
    public const string Kind = "client.character-list";

    public string MessageKind => Kind;

    // Prints the kind only; never the token.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("MessageKind = ").Append(Kind);
        return true;
    }
}
