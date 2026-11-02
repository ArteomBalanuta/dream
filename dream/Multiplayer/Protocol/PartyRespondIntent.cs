namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, party.respond). Accept or decline the pending party invite.
public sealed record PartyRespondIntent(bool Accept) : IPlayerActionPayload;
