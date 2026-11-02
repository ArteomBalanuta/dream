namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, trade.confirm). Confirm after both sides locked; the second confirm commits.
public sealed record TradeConfirmIntent() : IPlayerActionPayload;
