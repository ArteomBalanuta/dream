using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Runtime;

// Player to player trade (M5 workstream A). Contract stubs: every trade action is refused until
// workstream A fills this file in.
public sealed partial class WorldSessionRuntime
{
    private AuthoritativeActionResult ProcessTradeRequestAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeRequestIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");

    private AuthoritativeActionResult ProcessTradeRespondAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeRespondIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");

    private AuthoritativeActionResult ProcessTradeOfferAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeOfferIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");

    private AuthoritativeActionResult ProcessTradeLockAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeLockIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");

    private AuthoritativeActionResult ProcessTradeConfirmAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeConfirmIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");

    private AuthoritativeActionResult ProcessTradeCancelAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        TradeCancelIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.TradeUnavailable, "trade");

    // Cancels trades that went out of range, timed out or saw a revision change.
    private void StepTrades(ulong tick)
    {
    }

    private void CancelTradeOnDetach(PlayerSessionId session, string characterKey)
    {
    }
}
