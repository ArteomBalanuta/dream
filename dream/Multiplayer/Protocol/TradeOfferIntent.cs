namespace Dream.Multiplayer.Protocol;

// M5 (protocol v18, trade.offer). Replaces the sender's whole offer: bag items by instance id plus gold.
public sealed record TradeOfferIntent(IReadOnlyList<long> ItemInstanceIds, long Gold) : IPlayerActionPayload
{
    public const int MaxItems = 8;
}
