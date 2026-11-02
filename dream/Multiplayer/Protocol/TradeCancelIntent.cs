namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, trade.cancel). Cancel the open trade or request.
public sealed record TradeCancelIntent() : IPlayerActionPayload;
