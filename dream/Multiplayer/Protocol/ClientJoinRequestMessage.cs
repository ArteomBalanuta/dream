using System.Text;
using System.Text.Json.Serialization;
using Dream.Simulation.Characters;

namespace Dream.Multiplayer.Protocol;

// AccountToken is the player's secret (43 base64url characters). The server resolves it to
// ResolvedAccount in HandleClientMessage and replaces the message with one whose AccountToken is null,
// so the token is never queued, stored or printed. ResolvedAccount is never read from the wire.
public sealed record ClientJoinRequestMessage(
    string RequestedSessionId,
    string DisplayName,
    string? AccountToken = null,
    bool? HoldSpawnProtection = null) : IClientToServerMessage
{
    public string MessageKind => "client.join-request";

    [JsonIgnore]
    public AccountKey? ResolvedAccount { get; init; }

    // Prints the session and name only; never the token or the account.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("RequestedSessionId = ").Append(RequestedSessionId)
            .Append(", DisplayName = ").Append(DisplayName);
        return true;
    }
}
