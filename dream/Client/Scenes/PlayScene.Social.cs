using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;

namespace Dream.Client.Scenes;

// Party and trade on the client (M5 workstream A). The server is the authority for both; this side keeps
// what it was told, shows it and sends one intent per pick (through _hudIntents or the chat commands).
// Wire data is hostile: names and texts are cleaned and cut, counts are capped, numbers are clamped and a
// stage the window does not know closes it. Only primitive fields here, because the headless play scene
// skips field initialisers.
public partial class PlayScene
{
    // Set when this side asked the server to end the trade (the window was closed or lost its footing), so a
    // late state of that same trade does not reopen the window. Cleared by the trade's end and by a new
    // request or invitation.
    private bool _tradeCancelSent;

    // Once a frame: closes the trade window when it no longer fits (died, lost the session, closed the
    // inventory under it) and asks the server to end the trade then.
    private void UpdatePartyTradeHud()
    {
        if (!_remoteClient.IsConnected)
        {
            _partyHud.Clear();
            _tradeHud.Close();
            _tradeCancelSent = false;
            return;
        }

        if (!_tradeHud.IsOpen)
        {
            return;
        }

        // An incoming request waits for the player to open the inventory (it is not opened for them), so a
        // closed inventory does not decline it; the server drops it after TradeRules.RequestTimeoutTicks.
        bool inventoryGone = !_settings.InventoryVisible && _tradeHud.Stage != TradeHudStage.Incoming;
        if (_deathTracker.IsDead || inventoryGone)
        {
            CancelTradeFromHere();
        }
    }

    private void CancelTradeFromHere()
    {
        TradeHudStage stage = _tradeHud.Stage;
        _tradeHud.Close();
        _tradeCancelSent = true;
        _hudIntents.Enqueue(stage == TradeHudStage.Incoming ? new TradeRespondIntent(false) : new TradeCancelIntent());
    }

    private void ApplyPartyState(ServerPartyState state)
    {
        if (state is null)
        {
            return;
        }

        IReadOnlyList<PartyMemberSnapshot>? members = state.Members;
        int count = Math.Min(members?.Count ?? 0, PartyHudState.MaxMembers);
        PartyMemberLine[] lines = new PartyMemberLine[count];
        for (int n = 0; n < count; n++)
        {
            PartyMemberSnapshot member = members![n];
            string name = ChatHudState.Sanitize(member.Name, PartyHudState.MaxNameChars);
            int maxHealth = Math.Clamp(member.MaxHealth, 1, 1_000_000);
            lines[n] = new PartyMemberLine(
                name.Length == 0 ? "?" : name,
                Math.Clamp(member.Level, 0, 999),
                Math.Clamp(member.CurrentHealth, 0, maxHealth),
                maxHealth,
                member.IsLeader);
        }

        string invite = ChatHudState.Sanitize(state.PendingInviteFrom, PartyHudState.MaxNameChars);
        _partyHud.Set(lines, invite.Length == 0 ? null : invite);
    }

    private void ApplyTradeState(ServerTradeState state)
    {
        if (state is null)
        {
            return;
        }

        string reason = ChatHudState.Sanitize(state.EndReason, ChatHudState.MaxTextChars);
        if (state.Stage is TradeStage.Completed or TradeStage.Cancelled or TradeStage.None || !Enum.IsDefined(state.Stage))
        {
            bool wasOpen = _tradeHud.IsOpen || _tradeCancelSent;
            _tradeHud.Close();
            _tradeCancelSent = false;
            if (reason.Length > 0 && (wasOpen || state.Stage != TradeStage.None))
            {
                _chatInput.State.AddSystemLine(reason);
            }

            return;
        }

        TradeHudStage stage = state.Stage switch
        {
            TradeStage.Requested => TradeHudStage.Requested,
            TradeStage.Incoming => TradeHudStage.Incoming,
            _ => TradeHudStage.Open,
        };
        if (stage != TradeHudStage.Open)
        {
            // A new request or invitation is a new trade.
            _tradeCancelSent = false;
        }

        if (_tradeCancelSent || _deathTracker.IsDead || _settings.SettingsVisible)
        {
            return;
        }

        string partner = ChatHudState.Sanitize(state.Partner, TradeHudState.MaxPartnerChars);
        TradeHudLine[] mine = ReadTradeLines(state.MyItems);
        TradeHudLine[] theirs = ReadTradeLines(state.TheirItems);
        if (!_tradeHud.IsOpen && stage == TradeHudStage.Incoming && partner.Length > 0)
        {
            _chatInput.State.AddSystemLine($"{partner} wants to trade with you. Open your inventory to answer.");
        }

        // The window sits over the inventory. A request the player started or a trade they are in opens it;
        // an incoming request from someone else never throws the inventory over their screen (mid-fight, say):
        // the notice above says so, and the window is there once the player opens the inventory.
        if (stage != TradeHudStage.Incoming && !_settings.InventoryVisible)
        {
            _settings.ToggleInventory();
        }

        _tradeHud.Open(
            stage,
            partner.Length == 0 ? "Trade" : partner,
            mine,
            Math.Clamp(state.MyGold, 0L, InventoryConstants.MaxGold),
            state.MyLocked,
            state.MyConfirmed,
            theirs,
            Math.Clamp(state.TheirGold, 0L, InventoryConstants.MaxGold),
            state.TheirLocked,
            state.TheirConfirmed);
    }

    private static TradeHudLine[] ReadTradeLines(IReadOnlyList<TradeOfferLine>? source)
    {
        int count = Math.Min(source?.Count ?? 0, TradeHudState.MaxLines);
        List<TradeHudLine> lines = new(count);
        for (int n = 0; n < count; n++)
        {
            TradeOfferLine line = source![n];
            if (line.ItemInstanceId <= 0)
            {
                continue;
            }

            string text = ChatHudState.Sanitize(line.Summary, 128);
            ItemRarity rarity = Enum.IsDefined(line.Rarity) ? line.Rarity : ItemRarity.Common;
            lines.Add(new TradeHudLine(line.ItemInstanceId, rarity, text.Length == 0 ? "?" : text));
        }

        return [.. lines];
    }
}
