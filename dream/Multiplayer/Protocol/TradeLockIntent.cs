namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, trade.lock). Lock the sender's offer.
public sealed record TradeLockIntent() : IPlayerActionPayload;
