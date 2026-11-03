namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, trade.request). Ask the online player named TargetName to trade.
public sealed record TradeRequestIntent(string TargetName) : IPlayerActionPayload;
