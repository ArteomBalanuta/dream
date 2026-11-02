using Dream.Simulation.Inventory;

namespace Dream.Multiplayer.Protocol;

public enum TradeStage : byte
{
    None,
    Requested,
    Incoming,
    Open,
    Completed,
    Cancelled
}

// Summary is server-built stat text, at most 128 characters.
public readonly record struct TradeOfferLine(long ItemInstanceId, string ItemKind, ItemRarity Rarity, string Summary);

// M5 (protocol v18). Owner only, unprompted: the whole state of the receiver's trade. EndReason is set
// on Completed and Cancelled.
public sealed record ServerTradeState(
    TradeStage Stage,
    string Partner,
    IReadOnlyList<TradeOfferLine> MyItems,
    long MyGold,
    bool MyLocked,
    bool MyConfirmed,
    IReadOnlyList<TradeOfferLine> TheirItems,
    long TheirGold,
    bool TheirLocked,
    bool TheirConfirmed,
    string? EndReason) : IServerToClientMessage
{
    public const string Kind = "server.trade";
    public string MessageKind => Kind;
}
