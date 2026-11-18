using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Inventory;
using Dream.Simulation.Multiplayer;

namespace Dream.Simulation.Runtime;

// Player to player trade (M5 workstream A). The server keeps one TradeSession per character in memory (never
// persisted): Requested, Open, both lock, both confirm. The commit runs on the second confirm through
// PlayerInventoryService.TryExecuteTrade, as one inventory action that spends the confirming player's
// budget. Any change cancels: a difference in either inventory's fingerprint (an item moved, gold looted,
// anything that touched the bag or the wallet), an offer edit after a lock, walking out of range, death,
// a detach, a target that is gone, 120 s. The state goes to both sides as ServerTradeState through the
// session queue, once per tick while it changes and at once when the trade ends.
public sealed partial class WorldSessionRuntime
{
    private readonly Dictionary<string, TradeSession> _tradeOf = new(CharacterNames.Comparer);
    private readonly List<TradeSession> _trades = [];

    // The tick each character's last accepted trade request was opened at (TradeRules.RequestCooldownTicks),
    // the shape PartyRegistry._lastInviteTick has for invites. Dropped when the character leaves, so it
    // stays bounded by the players seen online.
    private readonly Dictionary<string, ulong> _lastTradeRequestTick = new(CharacterNames.Comparer);

    internal int ActiveTradeCount => _trades.Count;

    // Trade requests refused by the cooldown since this runtime started: a griefing loop is visible to an
    // operator through this and the server.trade.request-cooldown line.
    internal int TradeRequestCooldownRefusals { get; private set; }

    private bool TryGetTrade(ConnectedPlayer player, out TradeSession session)
        => _tradeOf.TryGetValue(player.CharacterKey.Value, out session!);

    // Rejects a trade action: a System line tells the player why, the result carries the outcome's reason.
    private AuthoritativeActionResult RejectTrade(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryEquipOutcome outcome,
        string reason)
    {
        SendSystemLine(player.SessionId, reason);
        return CompleteInventoryAction(message, player, outcome, "trade");
    }

    private AuthoritativeActionResult ProcessTradeRequestAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeRequestIntent intent)
    {
        if (!PartyRegistry.IsValidName(intent.TargetName))
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");
        }

        if (!TryFindSpawnedPlayerByName(intent.TargetName, out ConnectedPlayer target))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "That player is not online.");
        }

        if (ReferenceEquals(target, player) || CharacterNames.Comparer.Equals(target.CharacterKey.Value, player.CharacterKey.Value))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You cannot trade with yourself.");
        }

        if (TryGetTrade(player, out _))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You are already trading.");
        }

        if (TryGetTrade(target, out _))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, $"{target.DisplayName} is already trading.");
        }

        if (!TryGetTradePositions(player, target, out bool bothAlive, out bool inRange) || !bothAlive)
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You cannot trade right now.");
        }

        if (!inRange)
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TooFar, $"{target.DisplayName} is too far away to trade.");
        }

        // Last of the guards, so a refused request (offline, too far, already trading) is unaffected. The
        // cooldown was started by this sender's previous request ending before the target answered it
        // (CloseTrade), which is what a request/cancel loop does every time; a trade the target answered
        // starts none, so trading, cancelling and asking again stays immediate.
        if (_lastTradeRequestTick.TryGetValue(player.CharacterKey.Value, out ulong lastRequestTick)
            && CurrentServerTick - lastRequestTick < (ulong)TradeRules.RequestCooldownTicks)
        {
            TradeRequestCooldownRefusals++;
            NetworkDiagnosticsLog.Write(
                "server.trade.request-cooldown",
                $"session={player.SessionId.Value} target={target.DisplayName} tick={CurrentServerTick}");
            return RejectTrade(
                message,
                player,
                InventoryEquipOutcome.TradeUnavailable,
                "Wait a moment before asking to trade again.");
        }

        TradeSession session = new(player.CharacterKey.Value, target.CharacterKey.Value, CurrentServerTick);
        _tradeOf[player.CharacterKey.Value] = session;
        _tradeOf[target.CharacterKey.Value] = session;
        _trades.Add(session);
        return CreateAcceptedActionResult(message);
    }

    private AuthoritativeActionResult ProcessTradeRespondAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeRespondIntent intent)
    {
        if (!TryGetTrade(player, out TradeSession? session)
            || session.SideOf(player.CharacterKey.Value) != 1
            || session.Phase != TradePhase.Requested)
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You have no trade request.");
        }

        if (!intent.Accept)
        {
            EndTrade(session, TradeStage.Cancelled, $"{player.DisplayName} declined the trade.", "You declined the trade.");
            return CreateAcceptedActionResult(message);
        }

        if (!TryFindSpawnedPlayerByKey(session.Key(0), out ConnectedPlayer requester)
            || !TryGetTradePositions(requester, player, out bool bothAlive, out bool inRange)
            || !bothAlive
            || !inRange)
        {
            EndTrade(session, TradeStage.Cancelled, "The trade partner is too far away.", "The trade partner is too far away.");
            return CreateAcceptedActionResult(message);
        }

        if (!_inventory.Ensure(requester.CharacterKey.Value) || !_inventory.Ensure(player.CharacterKey.Value))
        {
            EndTrade(session, TradeStage.Cancelled, "An inventory is still loading.", "An inventory is still loading.");
            return CreateAcceptedActionResult(message);
        }

        session.Accept(player.CharacterKey.Value);
        session.Fingerprint0 = _inventory.TradeFingerprint(session.Key(0));
        session.Fingerprint1 = _inventory.TradeFingerprint(session.Key(1));
        return CreateAcceptedActionResult(message);
    }

    private AuthoritativeActionResult ProcessTradeOfferAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeOfferIntent intent)
    {
        if (!TryGetOpenTrade(player, out TradeSession? session))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You have no open trade.");
        }

        if (CancelIfChanged(session))
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");
        }

        if (!TradeRules.IsWellFormedOffer(intent.ItemInstanceIds, intent.Gold))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "That offer is not valid.");
        }

        // Once either side locked, the offers are final: an edit ends the trade.
        if (session.Locked(0) || session.Locked(1))
        {
            EndTrade(session, TradeStage.Cancelled, "The offer was changed after locking.", "The offer was changed after locking.");
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");
        }

        InventoryEquipOutcome gate = CheckTradeGates(session, player.CharacterKey.Value, intent.ItemInstanceIds, intent.Gold);
        if (gate != InventoryEquipOutcome.Equipped)
        {
            return FailTradeAction(message, player, session, gate);
        }

        session.SetOffer(player.CharacterKey.Value, intent.ItemInstanceIds, intent.Gold);
        return CreateAcceptedActionResult(message);
    }

    private AuthoritativeActionResult ProcessTradeLockAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeLockIntent intent)
    {
        _ = intent;
        if (!TryGetOpenTrade(player, out TradeSession? session))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You have no open trade.");
        }

        if (CancelIfChanged(session))
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");
        }

        // Both offers must still hold, and neither side may have a failed write on record.
        InventoryEquipOutcome gate = CheckBothOffers(session);
        if (gate != InventoryEquipOutcome.Equipped)
        {
            return FailTradeAction(message, player, session, gate);
        }

        TradeStepOutcome step = session.Lock(player.CharacterKey.Value);
        if (step != TradeStepOutcome.Ok)
        {
            return RejectTrade(
                message,
                player,
                InventoryEquipOutcome.TradeUnavailable,
                step == TradeStepOutcome.NothingOffered ? "Nothing is offered yet." : "You cannot lock now.");
        }

        return CreateAcceptedActionResult(message);
    }

    private AuthoritativeActionResult ProcessTradeConfirmAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeConfirmIntent intent)
    {
        _ = intent;
        if (!TryGetOpenTrade(player, out TradeSession? session))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You have no open trade.");
        }

        if (CancelIfChanged(session))
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");
        }

        TradeStepOutcome step = session.Confirm(player.CharacterKey.Value);
        if (step != TradeStepOutcome.Ok)
        {
            return RejectTrade(
                message,
                player,
                InventoryEquipOutcome.TradeUnavailable,
                step == TradeStepOutcome.AlreadyConfirmed ? "You already confirmed." : "Both sides must lock first.");
        }

        if (!session.BothConfirmed)
        {
            return CreateAcceptedActionResult(message);
        }

        // The commit. Both offers are checked once more, the trade is closed BEFORE the exchange so its own
        // inventory change does not cancel it, then one atomic exchange.
        InventoryEquipOutcome outcome = CheckBothOffers(session);
        string keyA = session.Key(0);
        string keyB = session.Key(1);
        long[] itemsA = [.. session.Items(0)];
        long[] itemsB = [.. session.Items(1)];
        long goldA = session.Gold(0);
        long goldB = session.Gold(1);
        CloseTrade(session);
        if (outcome == InventoryEquipOutcome.Equipped)
        {
            outcome = RunInventoryAction(
                player,
                static (service, _, trade) => service.TryExecuteTrade(trade.KeyA, trade.ItemsA, trade.GoldA, trade.KeyB, trade.ItemsB, trade.GoldB),
                (KeyA: keyA, ItemsA: itemsA, GoldA: goldA, KeyB: keyB, ItemsB: itemsB, GoldB: goldB));
        }

        if (outcome != InventoryEquipOutcome.Equipped)
        {
            string reason = TradeFailureText(outcome);
            SendTradeEnd(session, TradeStage.Cancelled, reason, reason);
            SendSystemLine(player.SessionId, reason);
            return CompleteInventoryAction(message, player, outcome, "trade");
        }

        SendTradeEnd(session, TradeStage.Completed, "Trade complete.", "Trade complete.");
        if (TryFindSpawnedPlayerByKey(keyA, out ConnectedPlayer first))
        {
            MarkInventoryPush(first.SessionId);
        }

        if (TryFindSpawnedPlayerByKey(keyB, out ConnectedPlayer second))
        {
            MarkInventoryPush(second.SessionId);
        }

        return CompleteInventoryAction(message, player, InventoryEquipOutcome.Equipped, "trade");
    }

    private AuthoritativeActionResult ProcessTradeCancelAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeCancelIntent intent)
    {
        _ = intent;
        if (!TryGetTrade(player, out TradeSession? session))
        {
            return RejectTrade(message, player, InventoryEquipOutcome.TradeUnavailable, "You have no trade to cancel.");
        }

        int side = session.SideOf(player.CharacterKey.Value);
        string mine = "You cancelled the trade.";
        string theirs = $"{player.DisplayName} cancelled the trade.";
        EndTrade(session, TradeStage.Cancelled, side == 0 ? mine : theirs, side == 0 ? theirs : mine);
        return CreateAcceptedActionResult(message);
    }

    // The open (both sides agreed) trade of the player.
    private bool TryGetOpenTrade(ConnectedPlayer player, out TradeSession session)
        => TryGetTrade(player, out session!) && session.Phase == TradePhase.Open;

    // Both gates for one side's new offer: the offer against its owner's inventory, the partner's inventory
    // against a failed write.
    private InventoryEquipOutcome CheckTradeGates(TradeSession session, string offerer, IReadOnlyList<long> items, long gold)
    {
        InventoryEquipOutcome outcome = _inventory.ValidateTradeOffer(offerer, items, gold);
        if (outcome != InventoryEquipOutcome.Equipped)
        {
            return outcome;
        }

        int side = session.SideOf(offerer);
        return _inventory.CheckTradeGate(session.Key(1 - side));
    }

    // Both stored offers against both inventories (item still a bag anchor item, gold still held, no failed
    // write on either side).
    private InventoryEquipOutcome CheckBothOffers(TradeSession session)
    {
        InventoryEquipOutcome outcome = _inventory.ValidateTradeOffer(session.Key(0), session.Items(0), session.Gold(0));
        return outcome != InventoryEquipOutcome.Equipped
            ? outcome
            : _inventory.ValidateTradeOffer(session.Key(1), session.Items(1), session.Gold(1));
    }

    // A failed write ends the trade (trading is paused for that inventory); any other failure refuses the
    // action and leaves the trade as it was.
    private AuthoritativeActionResult FailTradeAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeSession session,
        InventoryEquipOutcome outcome)
    {
        string reason = TradeFailureText(outcome);
        if (outcome == InventoryEquipOutcome.WriteFailed)
        {
            EndTrade(session, TradeStage.Cancelled, reason, reason);
        }
        else
        {
            SendSystemLine(player.SessionId, reason);
        }

        return CompleteInventoryAction(message, player, outcome, "trade");
    }

    private static string TradeFailureText(InventoryEquipOutcome outcome) => outcome switch
    {
        InventoryEquipOutcome.BackpackFull => "The trade does not fit: a backpack is full.",
        InventoryEquipOutcome.NotEnoughGold => "Someone does not have the gold offered.",
        InventoryEquipOutcome.WalletFull => "The trade would overfill a wallet.",
        InventoryEquipOutcome.WriteFailed => "An earlier inventory save failed, so trading is paused.",
        InventoryEquipOutcome.ItemGone => "An offered item is no longer in the bag.",
        InventoryEquipOutcome.Throttled => "Too many inventory actions, try again shortly.",
        InventoryEquipOutcome.StillLoading => "An inventory is still loading.",
        _ => "The trade was not valid.",
    };

    // Cancels the trade when either inventory differs from what it was when the trade opened.
    private bool CancelIfChanged(TradeSession session)
    {
        if (session.Phase != TradePhase.Open
            || (_inventory.TradeFingerprint(session.Key(0)) == session.Fingerprint0
                && _inventory.TradeFingerprint(session.Key(1)) == session.Fingerprint1))
        {
            return false;
        }

        EndTrade(session, TradeStage.Cancelled, "The trade was cancelled because an inventory changed.", "The trade was cancelled because an inventory changed.");
        return true;
    }

    // Both players' positions for a range check; false when either is not resolvable.
    private bool TryGetTradePositions(ConnectedPlayer first, ConnectedPlayer second, out bool bothAlive, out bool inRange)
    {
        bothAlive = false;
        inRange = false;
        if (!TryResolveAuthoritativePlayerTarget(first.SessionId, out _, out IAuthoritativePlayerTarget? a)
            || !TryResolveAuthoritativePlayerTarget(second.SessionId, out _, out IAuthoritativePlayerTarget? b)
            || a == null
            || b == null)
        {
            return false;
        }

        bothAlive = a.IsAlive && b.IsAlive;
        inRange = TradeRules.InRange(a.Position, b.Position);
        return true;
    }

    // Cancels trades that went out of range, timed out, lost a partner or saw an inventory change, and
    // sends the state of the ones that changed. A steady tick with no trade reads a count.
    private void StepTrades(ulong tick)
    {
        for (int i = _trades.Count - 1; i >= 0; i--)
        {
            if (i >= _trades.Count)
            {
                continue;
            }

            TradeSession session = _trades[i];
            if (!TryFindSpawnedPlayerByKey(session.Key(0), out ConnectedPlayer first)
                || !TryFindSpawnedPlayerByKey(session.Key(1), out ConnectedPlayer second))
            {
                EndTrade(session, TradeStage.Cancelled, "The trade partner is gone.", "The trade partner is gone.");
                continue;
            }

            if (session.Phase == TradePhase.Requested && tick - session.CreatedTick > (ulong)TradeRules.RequestTimeoutTicks)
            {
                EndTrade(session, TradeStage.Cancelled, "The trade request was not answered.", "The trade request was not answered.");
                continue;
            }

            if (tick - session.CreatedTick > (ulong)TradeRules.TimeoutTicks)
            {
                EndTrade(session, TradeStage.Cancelled, "The trade timed out.", "The trade timed out.");
                continue;
            }

            if (!TryGetTradePositions(first, second, out bool bothAlive, out bool inRange))
            {
                EndTrade(session, TradeStage.Cancelled, "The trade partner is gone.", "The trade partner is gone.");
                continue;
            }

            if (!bothAlive)
            {
                EndTrade(session, TradeStage.Cancelled, "The trade was cancelled because someone died.", "The trade was cancelled because someone died.");
                continue;
            }

            if (!inRange)
            {
                EndTrade(session, TradeStage.Cancelled, "The trade was cancelled because you moved apart.", "The trade was cancelled because you moved apart.");
                continue;
            }

            if (CancelIfChanged(session))
            {
                continue;
            }

            if (session.Dirty)
            {
                session.Dirty = false;
                SendTradeState(session, first, second);
            }
        }
    }

    private void CancelTradeOnDetach(PlayerSessionId session, string characterKey)
    {
        _ = session;
        if (_tradeOf.TryGetValue(characterKey, out TradeSession? trade))
        {
            string name = DisplayNameOfKey(characterKey);
            int side = trade.SideOf(characterKey);
            string left = $"{name} left the game.";
            EndTrade(trade, TradeStage.Cancelled, side == 0 ? "You left the game." : left, side == 0 ? left : "You left the game.");
        }

        // Last: the request cooldown goes with the character, and that EndTrade may have started one
        // (a rejoin costs far more than the 2 s it resets).
        _lastTradeRequestTick.Remove(characterKey);
    }

    // Ends the trade and tells both sides.
    private void EndTrade(TradeSession session, TradeStage stage, string reasonForFirst, string reasonForSecond)
    {
        CloseTrade(session);
        SendTradeEnd(session, stage, reasonForFirst, reasonForSecond);
    }

    private void CloseTrade(TradeSession session)
    {
        // A request the target never answered (still Requested when it closes: cancelled, declined or
        // timed out) starts the requester's cooldown. Read before Close, which moves the phase.
        if (session.Phase == TradePhase.Requested)
        {
            _lastTradeRequestTick[session.Key(0)] = CurrentServerTick;
        }

        session.Close();
        _trades.Remove(session);
        if (_tradeOf.TryGetValue(session.Key(0), out TradeSession? mapped0) && ReferenceEquals(mapped0, session))
        {
            _tradeOf.Remove(session.Key(0));
        }

        if (_tradeOf.TryGetValue(session.Key(1), out TradeSession? mapped1) && ReferenceEquals(mapped1, session))
        {
            _tradeOf.Remove(session.Key(1));
        }
    }

    private void SendTradeEnd(TradeSession session, TradeStage stage, string reasonForFirst, string reasonForSecond)
    {
        bool hasFirst = TryFindSpawnedPlayerByKey(session.Key(0), out ConnectedPlayer first);
        bool hasSecond = TryFindSpawnedPlayerByKey(session.Key(1), out ConnectedPlayer second);
        if (hasFirst)
        {
            QueueSessionMessage(first.SessionId, BuildTradeState(session, 0, stage, reasonForFirst));
        }

        if (hasSecond)
        {
            QueueSessionMessage(second.SessionId, BuildTradeState(session, 1, stage, reasonForSecond));
        }
    }

    private void SendTradeState(TradeSession session, ConnectedPlayer first, ConnectedPlayer second)
    {
        QueueSessionMessage(first.SessionId, BuildTradeState(session, 0, null, null));
        QueueSessionMessage(second.SessionId, BuildTradeState(session, 1, null, null));
    }

    // The state as one side sees it. stage null: the live stage from the session's phase.
    private ServerTradeState BuildTradeState(TradeSession session, int side, TradeStage? stage, string? endReason)
    {
        TradeStage live = stage ?? (session.Phase == TradePhase.Open
            ? TradeStage.Open
            : side == 0 ? TradeStage.Requested : TradeStage.Incoming);
        int other = 1 - side;
        return new ServerTradeState(
            live,
            DisplayNameOfKey(session.Key(other)),
            BuildTradeLines(session.Items(side)),
            session.Gold(side),
            session.Locked(side),
            session.Confirmed(side),
            BuildTradeLines(session.Items(other)),
            session.Gold(other),
            session.Locked(other),
            session.Confirmed(other),
            endReason);
    }

    private TradeOfferLine[] BuildTradeLines(IReadOnlyList<long> ids)
    {
        List<TradeOfferLine> lines = new(ids.Count);
        for (int i = 0; i < ids.Count && lines.Count < TradeRules.MaxItems; i++)
        {
            if (_inventory.TryDescribeTradeItem(ids[i], out string kind, out ItemRarity rarity, out string summary))
            {
                lines.Add(new TradeOfferLine(ids[i], kind, rarity, summary));
            }
        }

        return [.. lines];
    }
}
