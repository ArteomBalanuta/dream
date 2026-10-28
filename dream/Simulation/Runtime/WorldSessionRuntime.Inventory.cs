using System.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Runtime;

// Inventory: the session-facing side of the players' inventories. The state and logic (slots, item
// instances, item ids, equip, starter seeding, per-player load and eviction, failed-write
// retention, snapshots and equipped-tool modifiers) live in PlayerInventoryService; this partial
// maps sessions to player names, refreshes combat stats after a change and keeps the forwarders
// the join code (WorldSessionRuntime.Persistence.cs) calls. Eviction comes from
// RuntimePersistenceBridge through IRuntimePersistenceHost.EvictPlayerInventory.
public sealed partial class WorldSessionRuntime
{
    // Constructed in the constructor, before RestorePersistedWorldState seeds its item id counter.
    private readonly PlayerInventoryService _inventory;


    public PlayerInventorySnapshot? TryCaptureInventorySnapshot(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) || player == null)
        {
            return null;
        }

        if (!_inventory.Ensure(player.DisplayName))
        {
            return null;
        }

        RefreshAuthoritativePlayerCombatStats(sessionId);
        return _inventory.BuildSnapshot(player.DisplayName);
    }

    private AuthoritativeActionResult ProcessInventoryEquipAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryEquipIntent intent)
    {
        switch (_inventory.TryEquip(player.DisplayName, intent.SlotIndex))
        {
            case InventoryEquipOutcome.SlotOutOfRange:
                return CreateRejectedActionResult(message, "Inventory equip rejected because the slot index is out of range.");
            case InventoryEquipOutcome.StillLoading:
                return CreateRejectedActionResult(message, "Inventory equip rejected because the inventory is still loading.");
            case InventoryEquipOutcome.SlotEmpty:
                return CreateRejectedActionResult(message, "Inventory equip rejected because the slot is empty.");
            case InventoryEquipOutcome.Equipped:
                break;
            default:
                throw new UnreachableException();
        }

        RefreshAuthoritativePlayerCombatStats(message.SessionId);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new InventoryChangedResult(_inventory.BuildSnapshot(player.DisplayName)));
    }

    // Startup loads no inventory, only the item id counter (PlayerInventoryService.RestoreItemIdCounter).
    private void RestorePersistedInventoryState() => _inventory.RestoreItemIdCounter();

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

        target.Combatant.ApplyEquippedToolModifier(_inventory.ResolveEquippedToolModifier(player.DisplayName));
    }
}
