namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, trade.respond). Accept or decline the incoming trade request.
public sealed record TradeRespondIntent(bool Accept) : IPlayerActionPayload;
