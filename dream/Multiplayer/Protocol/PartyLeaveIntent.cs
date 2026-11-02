namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, party.leave). Leave the current party.
public sealed record PartyLeaveIntent() : IPlayerActionPayload;
