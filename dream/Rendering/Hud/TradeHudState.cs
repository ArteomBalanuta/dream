using Dream.Simulation.Inventory;

namespace Dream.Rendering.Hud;

// One offered item as the trade window shows it: the item instance id the offer intent names, the rarity
// for the colour and the server-built stat text, already cleaned and cut by the scene.
public readonly record struct TradeHudLine(long ItemInstanceId, ItemRarity Rarity, string Text);

// The stage the window is in; mirrors the part of the protocol's TradeStage that keeps a window open.
public enum TradeHudStage : byte
{
    Closed,

    // We asked and wait for the answer.
    Requested,

    // The other player asked: Accept and Decline.
    Incoming,

    // Both agreed: offers, lock, confirm.
    Open,
}

// What the trade window shows (M5 workstream A): both offers, the locks and the confirms. One instance per
// scene, created with a parameterless constructor (the headless play scene fills fields by it). The scene
// fills it from ServerTradeState; IsOpen is what ClassicHud reads to give the window the character window's
// place. The window only reads it and moves the bag page; nothing here is sent by itself.
public sealed class TradeHudState
{
    public const int MaxLines = 8;
    public const int MaxPartnerChars = 32;

    private readonly TradeHudLine[] _mine = new TradeHudLine[MaxLines];
    private readonly TradeHudLine[] _theirs = new TradeHudLine[MaxLines];

    public TradeHudStage Stage { get; private set; }

    public bool IsOpen => Stage != TradeHudStage.Closed;

    public string Partner { get; private set; } = string.Empty;

    public int MyCount { get; private set; }

    public int TheirCount { get; private set; }

    public long MyGold { get; private set; }

    public long TheirGold { get; private set; }

    public bool MyLocked { get; private set; }

    public bool MyConfirmed { get; private set; }

    public bool TheirLocked { get; private set; }

    public bool TheirConfirmed { get; private set; }

    // The bag list's page, 0-based. The window clamps it to the pages the bag has.
    public int BagPage { get; set; }

    public TradeHudLine MineAt(int index)
        => (uint)index < (uint)MyCount ? _mine[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public TradeHudLine TheirsAt(int index)
        => (uint)index < (uint)TheirCount ? _theirs[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public bool OffersItem(long itemInstanceId)
    {
        for (int i = 0; i < MyCount; i++)
        {
            if (_mine[i].ItemInstanceId == itemInstanceId)
            {
                return true;
            }
        }

        return false;
    }

    // Once either side locked, an offer edit would end the trade, so the window stops editing.
    public bool OfferEditable => Stage == TradeHudStage.Open && !MyLocked && !TheirLocked;

    public void Open(
        TradeHudStage stage,
        string partner,
        ReadOnlySpan<TradeHudLine> mine,
        long myGold,
        bool myLocked,
        bool myConfirmed,
        ReadOnlySpan<TradeHudLine> theirs,
        long theirGold,
        bool theirLocked,
        bool theirConfirmed)
    {
        if (Stage == TradeHudStage.Closed)
        {
            BagPage = 0;
        }

        Stage = stage;
        Partner = partner ?? string.Empty;
        MyCount = Fill(_mine, mine);
        TheirCount = Fill(_theirs, theirs);
        MyGold = myGold;
        TheirGold = theirGold;
        MyLocked = myLocked;
        MyConfirmed = myConfirmed;
        TheirLocked = theirLocked;
        TheirConfirmed = theirConfirmed;
    }

    public void Close()
    {
        Stage = TradeHudStage.Closed;
        Partner = string.Empty;
        Array.Clear(_mine);
        Array.Clear(_theirs);
        MyCount = 0;
        TheirCount = 0;
        MyGold = 0;
        TheirGold = 0;
        MyLocked = false;
        MyConfirmed = false;
        TheirLocked = false;
        TheirConfirmed = false;
        BagPage = 0;
    }

    private static int Fill(TradeHudLine[] target, ReadOnlySpan<TradeHudLine> source)
    {
        int count = Math.Min(source.Length, MaxLines);
        source[..count].CopyTo(target);
        Array.Clear(target, count, target.Length - count);
        return count;
    }
}
