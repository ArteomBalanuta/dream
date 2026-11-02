using Dream.Simulation.Characters;

namespace Dream.Simulation.Multiplayer;

public enum TradePhase : byte
{
    Requested,
    Open,
    Closed,
}

public enum TradeStepOutcome : byte
{
    Ok,
    NotInTrade,
    WrongPhase,
    BadOffer,

    // An offer edit after either side locked: the trade is over (the caller cancels it).
    EditAfterLock,
    NotLocked,
    AlreadyConfirmed,
    NothingOffered,
}

// One trade as a pure state machine (M5): Requested, then Open once the target accepts, then both lock,
// then both confirm. Offers may be edited freely until either side locks; an edit after that cancels the
// trade (the caller does it on EditAfterLock). Confirm needs both locks, and the commit is ready on the
// second confirm. It holds item ids, not items; the inventory checks happen in the service.
public sealed class TradeSession
{
    private readonly string[] _keys;
    private readonly List<long>[] _items = [[], []];
    private readonly long[] _gold = new long[2];
    private readonly bool[] _locked = new bool[2];
    private readonly bool[] _confirmed = new bool[2];

    public TradeSession(string requester, string target, ulong createdTick)
    {
        _keys = [requester, target];
        CreatedTick = createdTick;
    }

    public TradePhase Phase { get; private set; } = TradePhase.Requested;

    public ulong CreatedTick { get; }

    // What each inventory looked like when the trade opened (the service's TradeFingerprint); any
    // difference later cancels the trade.
    public long Fingerprint0 { get; set; }

    public long Fingerprint1 { get; set; }

    // Set when the state changed since the last message to the two sides.
    public bool Dirty { get; set; } = true;

    public string Key(int side) => _keys[side];

    public IReadOnlyList<long> Items(int side) => _items[side];

    public long Gold(int side) => _gold[side];

    public bool Locked(int side) => _locked[side];

    public bool Confirmed(int side) => _confirmed[side];

    public bool BothConfirmed => _confirmed[0] && _confirmed[1];

    public bool BothEmpty => _items[0].Count == 0 && _items[1].Count == 0 && _gold[0] == 0 && _gold[1] == 0;

    // 0 for the requester, 1 for the target, -1 for anyone else.
    public int SideOf(string key)
    {
        if (CharacterNames.Comparer.Equals(_keys[0], key))
        {
            return 0;
        }

        return CharacterNames.Comparer.Equals(_keys[1], key) ? 1 : -1;
    }

    // The target accepts a request.
    public TradeStepOutcome Accept(string key)
    {
        if (SideOf(key) != 1)
        {
            return TradeStepOutcome.NotInTrade;
        }

        if (Phase != TradePhase.Requested)
        {
            return TradeStepOutcome.WrongPhase;
        }

        Phase = TradePhase.Open;
        Dirty = true;
        return TradeStepOutcome.Ok;
    }

    // Replaces the side's whole offer.
    public TradeStepOutcome SetOffer(string key, IReadOnlyList<long>? itemIds, long gold)
    {
        int side = SideOf(key);
        if (side < 0)
        {
            return TradeStepOutcome.NotInTrade;
        }

        if (Phase != TradePhase.Open)
        {
            return TradeStepOutcome.WrongPhase;
        }

        if (!TradeRules.IsWellFormedOffer(itemIds, gold))
        {
            return TradeStepOutcome.BadOffer;
        }

        if (_locked[0] || _locked[1])
        {
            return TradeStepOutcome.EditAfterLock;
        }

        _items[side].Clear();
        _items[side].AddRange(itemIds!);
        _gold[side] = gold;
        Dirty = true;
        return TradeStepOutcome.Ok;
    }

    public TradeStepOutcome Lock(string key)
    {
        int side = SideOf(key);
        if (side < 0)
        {
            return TradeStepOutcome.NotInTrade;
        }

        if (Phase != TradePhase.Open)
        {
            return TradeStepOutcome.WrongPhase;
        }

        if (BothEmpty)
        {
            return TradeStepOutcome.NothingOffered;
        }

        if (!_locked[side])
        {
            _locked[side] = true;
            Dirty = true;
        }

        return TradeStepOutcome.Ok;
    }

    public TradeStepOutcome Confirm(string key)
    {
        int side = SideOf(key);
        if (side < 0)
        {
            return TradeStepOutcome.NotInTrade;
        }

        if (Phase != TradePhase.Open)
        {
            return TradeStepOutcome.WrongPhase;
        }

        if (!_locked[0] || !_locked[1])
        {
            return TradeStepOutcome.NotLocked;
        }

        if (_confirmed[side])
        {
            return TradeStepOutcome.AlreadyConfirmed;
        }

        _confirmed[side] = true;
        Dirty = true;
        return TradeStepOutcome.Ok;
    }

    public void Close()
    {
        Phase = TradePhase.Closed;
        Dirty = true;
    }
}
