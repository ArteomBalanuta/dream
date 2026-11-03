using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Runtime;

// Inventory: the session-facing side of the players' inventories. The state and logic (slots, item
// instances, item ids, bag placement, equip, starter seeding, per-player load and eviction,
// failed-write retention, snapshots and equipped-tool modifiers) live in PlayerInventoryService; this partial
// maps sessions to player names, refreshes combat stats after a change and keeps the forwarders
// the join code (WorldSessionRuntime.Persistence.cs) calls. Eviction comes from
// RuntimePersistenceBridge through IRuntimePersistenceHost.EvictPlayerInventory.
public sealed partial class WorldSessionRuntime
{
    // Constructed in the constructor, before RestorePersistedWorldState seeds its item id counter.
    private readonly PlayerInventoryService _inventory;

    // Reused by RefreshAuthoritativePlayerCombatStats (main thread, never re-entered).
    private readonly EquipmentLoadout _equipmentScratch = new();


    public PlayerInventorySnapshot? TryCaptureInventorySnapshot(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) || player == null)
        {
            return null;
        }

        if (!_inventory.Ensure(player.CharacterKey.Value))
        {
            return null;
        }

        RefreshAuthoritativePlayerCombatStats(sessionId);
        return _inventory.BuildSnapshot(player.CharacterKey.Value);
    }

    // The session whose inventory action is being processed; its budget is the one spent (H-D18).
    // Set only by RunInventoryAction, around the service call and on the main thread, and every client
    // inventory action goes through it. The service calls the gate inside that call, once it has decided
    // the action would write, so the gate cannot run for any other session. Unset means no client
    // action is running (a test driving the service directly), which has no budget to spend.
    private ConnectedPlayer? _inventoryActionPlayer;

    private AuthoritativeActionResult ProcessInventoryEquipAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryEquipIntent intent)
    {
        InventoryEquipOutcome outcome = RunInventoryAction(
            player,
            static (service, key, i) => service.TryEquip(key, i.SlotIndex, i.TargetSlotIndex),
            intent);
        return CompleteInventoryAction(message, player, outcome, "equip");
    }

    private AuthoritativeActionResult ProcessInventoryUnequipAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryUnequipIntent intent)
    {
        InventoryEquipOutcome outcome = RunInventoryAction(
            player,
            static (service, key, i) => service.TryUnequip(key, i.SlotIndex, i.TargetCell),
            intent);
        return CompleteInventoryAction(message, player, outcome, "unequip");
    }

    private AuthoritativeActionResult ProcessInventoryMoveAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryMoveIntent intent)
    {
        InventoryEquipOutcome outcome = RunInventoryAction(
            player,
            static (service, key, i) => service.TryMove(key, i.From, i.To),
            intent);
        return CompleteInventoryAction(message, player, outcome, "move");
    }

    private AuthoritativeActionResult ProcessInventoryDestroyAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryDestroyIntent intent)
    {
        InventoryEquipOutcome outcome = RunInventoryAction(
            player,
            static (service, key, i) => service.TryDestroy(key, i.SlotIndex, i.ItemInstanceId),
            intent);
        return CompleteInventoryAction(message, player, outcome, "destroy");
    }

    private InventoryEquipOutcome RunInventoryAction<TIntent>(
        ConnectedPlayer player,
        Func<PlayerInventoryService, string, TIntent, InventoryEquipOutcome> action,
        TIntent intent)
    {
        _inventoryActionPlayer = player;
        try
        {
            return action(_inventory, player.CharacterKey.Value, intent);
        }
        finally
        {
            _inventoryActionPlayer = null;
        }
    }

    private bool SpendInventoryActionBudget()
        => _inventoryActionPlayer == null || _inventoryActionPlayer.InventoryBudget.TrySpend(CurrentServerTick);

    private AuthoritativeActionResult CompleteInventoryAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryEquipOutcome outcome,
        string verb)
    {
        string? reason = outcome switch
        {
            InventoryEquipOutcome.Equipped => null,
            InventoryEquipOutcome.SlotOutOfRange => "the slot index is out of range",
            InventoryEquipOutcome.StillLoading => "the inventory is still loading",
            InventoryEquipOutcome.SlotEmpty => "the slot is empty",
            InventoryEquipOutcome.TargetOutOfRange => "the target slot is not an equipment slot",
            InventoryEquipOutcome.NotEquippable => "the item cannot be used that way",
            InventoryEquipOutcome.BackpackFull => "the backpack is full",
            InventoryEquipOutcome.ItemEquipped => "the item is equipped",
            InventoryEquipOutcome.ItemMismatch => "the slot no longer holds that item",
            InventoryEquipOutcome.Throttled => "too many inventory actions, try again shortly",
            InventoryEquipOutcome.NoRoom => "the item does not fit there",
            InventoryEquipOutcome.TooFar => "it is too far away",
            InventoryEquipOutcome.NotYours => "it is reserved for another player",
            InventoryEquipOutcome.ItemGone => "the item is gone",
            InventoryEquipOutcome.NotEnoughGold => "there is not enough gold",
            InventoryEquipOutcome.WalletFull => "the wallet is full",
            InventoryEquipOutcome.NotSellable => "the item cannot be sold",
            InventoryEquipOutcome.NotInStock => "the vendor does not sell that",
            InventoryEquipOutcome.OnCooldown => "the item is on cooldown",
            InventoryEquipOutcome.NothingToRestore => "there is nothing to restore",
            InventoryEquipOutcome.WriteFailed => "an earlier inventory save failed, so inventory changes are paused",
            InventoryEquipOutcome.TradeUnavailable => "the trade is not available",
            InventoryEquipOutcome.QuestUnavailable => "the quest is not available",
            _ => "the request was not valid",
        };
        if (reason != null)
        {
            return CreateRejectedActionResult(message, $"Inventory {verb} rejected because {reason}.");
        }

        RefreshAuthoritativePlayerCombatStats(message.SessionId);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new InventoryChangedResult(_inventory.BuildSnapshot(player.CharacterKey.Value)));
    }

    // See PlayerInventoryService.ApplyLoaded.
    private void ApplyLoadedInventory(string playerName, PersistedPlayerInventory? inventory)
        => _inventory.ApplyLoaded(playerName, inventory);

    // Drops a departed player's loaded inventory, or keeps it while its writes may have failed; see
    // PlayerInventoryService.Evict.
    private void EvictPlayerInventory(string playerName) => _inventory.Evict(playerName);

    // Evicted inventories still held until their writes are confirmed.
    internal int UnconfirmedInventoryEvictionCount => _inventory.UnconfirmedEvictionCount;

    internal int LoadedInventoryCount => _inventory.LoadedInventoryCount;

    internal int LoadedItemInstanceCount => _inventory.LoadedItemInstanceCount;

    // The inventory service itself, for tests that grant rolled items.
    internal PlayerInventoryService InventoryService => _inventory;

    internal long NextItemInstanceId => _inventory.NextItemInstanceId;

    // False when the inventory could not be loaded yet; see PlayerInventoryService.Ensure.
    private bool EnsurePlayerInventory(string playerName) => _inventory.Ensure(playerName);

    private void RefreshAuthoritativePlayerCombatStats(PlayerSessionId sessionId)
    {
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? target)
            || player == null
            || target == null)
        {
            return;
        }

        _inventory.CopyEquipment(player.CharacterKey.Value, _equipmentScratch);
        target.Combatant.ApplyEquipment(_equipmentScratch);
    }
}
