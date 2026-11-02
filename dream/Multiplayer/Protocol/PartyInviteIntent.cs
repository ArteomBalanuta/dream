namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, party.invite). Invite the online player named TargetName to the sender's party.
public sealed record PartyInviteIntent(string TargetName) : IPlayerActionPayload;
