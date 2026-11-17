using System.Globalization;
using System.Text;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Inventory;

// Player to player trade (M5 workstream A): the offer checks, the inventory fingerprint a trade watches, the
// description of an offered item and the one atomic exchange. The trade state machine and its messages live
// in the runtime (WorldSessionRuntime.Trade.cs); this side only touches inventories.
internal sealed partial class PlayerInventoryService
{
    // The longest summary an offer line carries; the protocol caps it at 128 characters.
    internal const int MaxTradeSummaryChars = 128;

    // A number that changes whenever the player's bag, worn rows or wallet change, whatever changed them.
    // It is computed from the rows themselves rather than counted per write, so no write route (a pickup,
    // a kill's gold, a vendor deal, a quest reward, a move or an equip) can bypass it: an open trade
    // compares it with the value it saw when it opened and cancels on any difference. The sum of mixed
    // (slot, item) pairs does not depend on the iteration order. 0 for an inventory that is not loaded.
    internal long TradeFingerprint(string player)
    {
        if (!_slotsByPlayer.TryGetValue(player, out Dictionary<int, long>? slots))
        {
            return 0;
        }

        ulong sum = Mix(0x77616C6C65745F67UL, (ulong)GoldOf(player));
        foreach (KeyValuePair<int, long> row in slots)
        {
            sum += Mix((ulong)(row.Key + 1), (ulong)row.Value);
        }

        return (long)(sum | 1UL);
    }

    private static ulong Mix(ulong a, ulong b)
    {
        ulong x = (a * 0x9E3779B97F4A7C15UL) ^ (b + 0xBF58476D1CE4E5B9UL + (a << 6) + (a >> 2));
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }

    // Whether the inventory is loaded and has no failed write on record: the trade's fast refusal at offer,
    // lock and commit (the throwing guards of the closure stay the real backstop, because a write still
    // queued can fail after this check).
    internal InventoryEquipOutcome CheckTradeGate(string player)
    {
        if (!Ensure(player))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        return HasSeenFailedWrite(player) ? InventoryEquipOutcome.WriteFailed : InventoryEquipOutcome.Equipped;
    }

    // Checks one side's offer against the player's inventory: every id is an item at a bag anchor (0..63) of
    // the player, not worn and not a held-back row, and the gold is held. The list is checked for size and
    // duplicates before any lookup. Equipped when it holds.
    internal InventoryEquipOutcome ValidateTradeOffer(string player, IReadOnlyList<long>? itemIds, long gold)
    {
        InventoryEquipOutcome gate = CheckTradeGate(player);
        if (gate != InventoryEquipOutcome.Equipped)
        {
            return gate;
        }

        if (itemIds == null || itemIds.Count > Dream.Simulation.Multiplayer.TradeRules.MaxItems
            || gold < 0 || gold > InventoryConstants.MaxGold)
        {
            return InventoryEquipOutcome.TradeUnavailable;
        }

        if (gold > GoldOf(player))
        {
            return InventoryEquipOutcome.NotEnoughGold;
        }

        Dictionary<int, long> slots = GetOrCreateSlots(player);
        for (int i = 0; i < itemIds.Count; i++)
        {
            long id = itemIds[i];
            if (id <= 0)
            {
                return InventoryEquipOutcome.TradeUnavailable;
            }

            for (int j = 0; j < i; j++)
            {
                if (itemIds[j] == id)
                {
                    return InventoryEquipOutcome.TradeUnavailable;
                }
            }

            if (FindBagAnchorOf(slots, id) < 0 || !_itemInstances.ContainsKey(id))
            {
                return InventoryEquipOutcome.ItemGone;
            }

            // An item that also sits in an equipment slot (only a hand-edited row) is refused, as the
            // destroy, sell, drop and consume paths refuse it: the commit takes the bag anchor off only,
            // so trading it would leave the giver wearing a copy the receiver now holds.
            if (FindEquipmentSlotOf(slots, id) >= 0)
            {
                return InventoryEquipOutcome.ItemEquipped;
            }
        }

        return InventoryEquipOutcome.Equipped;
    }

    // The bag anchor (0..63) holding the item, or -1 (not in the bag, worn, held back or parked).
    private static int FindBagAnchorOf(Dictionary<int, long> slots, long itemInstanceId)
    {
        for (int anchor = 0; anchor < InventoryConstants.BackpackSlotCount; anchor++)
        {
            if (slots.TryGetValue(anchor, out long held) && held == itemInstanceId)
            {
                return anchor;
            }
        }

        return -1;
    }

    // What an offered item looks like to the other side: kind, rarity and a short stat text. False when the
    // item is not a loaded item.
    internal bool TryDescribeTradeItem(long itemInstanceId, out string itemKind, out ItemRarity rarity, out string summary)
    {
        itemKind = string.Empty;
        rarity = ItemRarity.Common;
        summary = string.Empty;
        if (!_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return false;
        }

        itemKind = item.ItemKind;
        rarity = item.Roll?.Rarity ?? ItemRarity.Common;
        StringBuilder text = new();
        text.Append(Sanitized(item.DisplayName));
        if (ContentCatalog.Shipped.TryGetItem(item.ItemKind, out ItemDefinition? definition))
        {
            IReadOnlyList<StatModifier> modifiers = item.Roll != null ? item.Roll.Modifiers : definition.Modifiers;
            for (int i = 0; i < modifiers.Count && text.Length < MaxTradeSummaryChars; i++)
            {
                StatModifier m = modifiers[i];
                text.Append(i == 0 ? ": " : ", ");
                text.Append(m.Stat).Append(' ');
                text.Append(m.Value >= 0 ? '+' : '-');
                text.Append(Math.Abs(m.Value).ToString("0.##", CultureInfo.InvariantCulture));
                if (m.Op == StatOp.Percent)
                {
                    text.Append('%');
                }
            }
        }

        summary = text.Length > MaxTradeSummaryChars ? text.ToString(0, MaxTradeSummaryChars) : text.ToString();
        return true;
    }

    private static string Sanitized(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder clean = new(value.Length);
        foreach (char c in value)
        {
            if (!char.IsControl(c))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    private readonly record struct TradeMove(string To, long ItemInstanceId, int Slot);

    // Swaps the offers of a and b and their gold in one step. The checks come first and change nothing:
    //   1. both inventories loaded, no failed write on record on either side (WriteFailed);
    //   2. every id is still a bag anchor item of its owner, not worn, none on both sides;
    //   3. each side holds the gold it offers and the other side's wallet takes what it receives;
    //   4. each recipient's bag, with its outgoing items taken off, fits every incoming item (BackpackFull);
    //   5. the action budget (Throttled).
    // Then memory changes and ONE closure is queued, in the order that makes a failure harmless: the debits
    // (a wallet that cannot pay aborts on its CHECK before any item moves), every delete of an outgoing
    // slot row, the guarded inserts (an item with no row to detach, or an occupied slot, throws), and the
    // credits last. The queue's savepoint lands all of it or none. NoteWriteQueued runs for both players
    // before the closure is queued, so a failed closure pauses both inventories until a restart reloads the
    // true rows. Never DestroyHeldInventoryItem (it deletes the item row) and never SaveInventorySlot (the
    // upsert can overwrite a row).
    internal InventoryEquipOutcome TryExecuteTrade(
        string a,
        IReadOnlyList<long> aItems,
        long aGold,
        string b,
        IReadOnlyList<long> bItems,
        long bGold)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return InventoryEquipOutcome.TradeUnavailable;
        }

        InventoryEquipOutcome outcome = ValidateTradeOffer(a, aItems, aGold);
        if (outcome != InventoryEquipOutcome.Equipped)
        {
            return outcome;
        }

        outcome = ValidateTradeOffer(b, bItems, bGold);
        if (outcome != InventoryEquipOutcome.Equipped)
        {
            return outcome;
        }

        // An item on both sides cannot be held by both owners, so ValidateTradeOffer already refused it
        // (ItemGone); this stays as the explicit guard.
        foreach (long id in aItems)
        {
            if (bItems.Contains(id))
            {
                return InventoryEquipOutcome.TradeUnavailable;
            }
        }

        long aGoldNow = GoldOf(a);
        long bGoldNow = GoldOf(b);
        if (bGold > InventoryConstants.MaxGold - (aGoldNow - aGold)
            || aGold > InventoryConstants.MaxGold - (bGoldNow - bGold))
        {
            return InventoryEquipOutcome.WalletFull;
        }

        Dictionary<int, long> aSlots = GetOrCreateSlots(a);
        Dictionary<int, long> bSlots = GetOrCreateSlots(b);
        List<TradeMove> moves = new(aItems.Count + bItems.Count);
        if (!PlanIncoming(a, aSlots, aItems, bItems, moves)
            || !PlanIncoming(b, bSlots, bItems, aItems, moves))
        {
            return InventoryEquipOutcome.BackpackFull;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        // Memory. Take every outgoing row off first, then place the incoming ones.
        List<int> aAnchors = AnchorsOf(aSlots, aItems);
        List<int> bAnchors = AnchorsOf(bSlots, bItems);
        foreach (int anchor in aAnchors)
        {
            aSlots.Remove(anchor);
        }

        foreach (int anchor in bAnchors)
        {
            bSlots.Remove(anchor);
        }

        DateTime utc = DateTime.UtcNow;
        foreach (TradeMove move in moves)
        {
            Dictionary<int, long> target = string.Equals(move.To, a, StringComparison.Ordinal) ? aSlots : bSlots;
            target[move.Slot] = move.ItemInstanceId;
            _itemInstances[move.ItemInstanceId] = _itemInstances[move.ItemInstanceId] with
            {
                OwnerPlayerName = move.To,
                WorldPosition = null,
                State = "Inventory",
                UpdatedUtc = utc,
            };
        }

        _goldByPlayer[a] = aGoldNow - aGold + bGold;
        _goldByPlayer[b] = bGoldNow - bGold + aGold;

        long[] aIds = [.. aItems];
        long[] bIds = [.. bItems];
        TradeMove[] plan = [.. moves];
        NoteWriteQueued(a);
        NoteWriteQueued(b);
        EnqueueWorldWrite(world =>
        {
            // Debits first: a wallet that cannot pay (CHECK gold >= 0) aborts before any item moves.
            if (aGold > 0)
            {
                world.ApplyWalletDelta(a, -aGold);
            }

            if (bGold > 0)
            {
                world.ApplyWalletDelta(b, -bGold);
            }

            // Every delete before any insert: a swap reuses the slots just vacated. Each throws when the
            // player has no slot row for the item (a phantom from an earlier failed write).
            foreach (long id in aIds)
            {
                world.DetachHeldItem(a, id);
            }

            foreach (long id in bIds)
            {
                world.DetachHeldItem(b, id);
            }

            foreach (TradeMove move in plan)
            {
                world.AttachItem(move.To, move.ItemInstanceId, move.Slot, utc);
            }

            // Credits last.
            if (bGold > 0)
            {
                world.ApplyWalletDelta(a, bGold);
            }

            if (aGold > 0)
            {
                world.ApplyWalletDelta(b, aGold);
            }
        });
        RefreshEquippedSummary(a);
        RefreshEquippedSummary(b);
        return InventoryEquipOutcome.Equipped;
    }

    private static List<int> AnchorsOf(Dictionary<int, long> slots, IReadOnlyList<long> items)
    {
        List<int> anchors = new(items.Count);
        foreach (long id in items)
        {
            anchors.Add(FindBagAnchorOf(slots, id));
        }

        return anchors;
    }

    // Finds a place in the recipient's bag for every item that comes in, on the bag with the outgoing items
    // taken off. Appends the moves; false when one does not fit.
    private bool PlanIncoming(
        string recipient,
        Dictionary<int, long> recipientSlots,
        IReadOnlyList<long> outgoing,
        IReadOnlyList<long> incoming,
        List<TradeMove> moves)
    {
        if (incoming.Count == 0)
        {
            return true;
        }

        BagBoard board = BuildBoard(recipientSlots);
        foreach (long id in outgoing)
        {
            int anchor = FindBagAnchorOf(recipientSlots, id);
            if (anchor < 0 || !board.Remove(anchor))
            {
                return false;
            }
        }

        foreach (long id in incoming)
        {
            ItemFootprint footprint = FootprintOfItem(id);
            int fit = board.FirstFit(footprint);
            if (fit < 0 || !board.TryPlace(fit, footprint))
            {
                return false;
            }

            moves.Add(new TradeMove(recipient, id, fit));
        }

        return true;
    }
}
