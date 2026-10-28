using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Runtime;

// Inventory: inventory snapshots, equip actions, starter inventory, slot persistence and
// equipped-tool combat stats.
public sealed partial class WorldSessionRuntime
{
    // Loaded inventories only: a player's inventory is loaded when it joins (with the join
    // prefetch, see RestorePersistedPlayerState) and evicted with its profile once it has departed
    // (EvictCachedProfile), so these hold the active players' and the recently departed players'
    // inventories, not every inventory in the database. A name is a key of
    // _inventorySlotsByPlayer exactly when its inventory is loaded (possibly empty).
    private readonly Dictionary<long, PersistedItemInstance> _itemInstances = [];
    private readonly Dictionary<string, Dictionary<int, long>> _inventorySlotsByPlayer = new(StringComparer.OrdinalIgnoreCase);

    private long _nextItemInstanceId = 1;

    public PlayerInventorySnapshot? TryCaptureInventorySnapshot(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) || player == null)
        {
            return null;
        }

        if (!EnsurePlayerInventory(player.DisplayName))
        {
            return null;
        }

        RefreshAuthoritativePlayerCombatStats(sessionId);
        return BuildInventorySnapshot(player.DisplayName);
    }

    private AuthoritativeActionResult ProcessInventoryEquipAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryEquipIntent intent)
    {
        if (intent.SlotIndex < 0 || intent.SlotIndex >= InventoryConstants.BackpackSlotCount)
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the slot index is out of range.");
        }

        if (!EnsurePlayerInventory(player.DisplayName))
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the inventory is still loading.");
        }

        if (!_inventorySlotsByPlayer.TryGetValue(player.DisplayName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(intent.SlotIndex, out long itemInstanceId)
            || !_itemInstances.ContainsKey(itemInstanceId))
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the slot is empty.");
        }

        slots[InventoryConstants.EquippedSlotIndex] = itemInstanceId;
        PersistInventorySlot(player.DisplayName, InventoryConstants.EquippedSlotIndex, itemInstanceId);
        RefreshAuthoritativePlayerCombatStats(message.SessionId);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new InventoryChangedResult(BuildInventorySnapshot(player.DisplayName)));
    }

    // Startup loads no inventory, only the item id counter: new ids start above every persisted id,
    // including those of inventories that are never loaded this run.
    private void RestorePersistedInventoryState()
    {
        if (_worldPersistence == null)
        {
            return;
        }

        _nextItemInstanceId = Math.Max(_nextItemInstanceId, _worldPersistence.GetMaxItemInstanceId() + 1);
    }

    // Installs a player's loaded inventory unless one is already loaded for the name: a loaded
    // inventory is at least as new as any row a load read, because every change to it queues its
    // write when it happens and an evicted inventory's loaded prefetch is dropped (EvictCachedProfile).
    private void ApplyLoadedInventory(string playerName, PersistedPlayerInventory? inventory)
    {
        if (inventory == null || _inventorySlotsByPlayer.ContainsKey(playerName))
        {
            return;
        }

        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        foreach (PersistedItemInstance item in inventory.Items)
        {
            _itemInstances[item.ItemInstanceId] = item;
            _nextItemInstanceId = Math.Max(_nextItemInstanceId, item.ItemInstanceId + 1);
        }

        foreach (PersistedInventorySlot slot in inventory.Slots)
        {
            slots[slot.SlotIndex] = slot.ItemInstanceId;
            _nextItemInstanceId = Math.Max(_nextItemInstanceId, slot.ItemInstanceId + 1);
        }
    }

    // Loads a player's inventory that no join loaded (a direct caller of a runtime without profiles,
    // such as a tool or test): the queued writes are flushed first so the read is current. False
    // when they did not drain in time; the inventory then stays unloaded, so no starter inventory
    // is seeded over rows still being written.
    private bool TryLoadInventorySynchronously(string playerName)
    {
        if (_inventorySlotsByPlayer.ContainsKey(playerName))
        {
            return true;
        }

        if (_worldPersistence == null)
        {
            return true;
        }

        NetworkDiagnosticsLog.Write("server.persistence.inventory-sync-read", $"player={playerName}");
        if (_writes != null && !_writes.Flush(TimeSpan.FromSeconds(5)))
        {
            NetworkDiagnosticsLog.Write("server.persistence.inventory-sync-read-timeout", $"player={playerName}");
            return false;
        }

        ApplyLoadedInventory(playerName, _worldPersistence.LoadPlayerInventory(playerName));
        return true;
    }

    // Drops a departed player's loaded inventory (EvictCachedProfile). Nothing is lost: its
    // changes were queued as they happened.
    private void EvictPlayerInventory(string playerName)
    {
        if (!_inventorySlotsByPlayer.Remove(playerName, out Dictionary<int, long>? slots))
        {
            return;
        }

        foreach (long itemInstanceId in slots.Values)
        {
            _itemInstances.Remove(itemInstanceId);
        }
    }

    internal int LoadedInventoryCount => _inventorySlotsByPlayer.Count;

    internal int LoadedItemInstanceCount => _itemInstances.Count;

    internal long NextItemInstanceId => _nextItemInstanceId;

    // False when the inventory could not be loaded yet (see TryLoadInventorySynchronously).
    private bool EnsurePlayerInventory(string playerName)
    {
        if (!TryLoadInventorySynchronously(playerName))
        {
            return false;
        }

        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        bool hasBackpackContent = slots.Keys.Any(slotIndex => slotIndex >= 0 && slotIndex < InventoryConstants.BackpackSlotCount);
        if (hasBackpackContent)
        {
            if (!slots.ContainsKey(InventoryConstants.EquippedSlotIndex))
            {
                KeyValuePair<int, long> firstOccupiedSlot = slots
                    .Where(entry => entry.Key >= 0 && entry.Key < InventoryConstants.BackpackSlotCount)
                    .OrderBy(entry => entry.Key)
                    .First();
                slots[InventoryConstants.EquippedSlotIndex] = firstOccupiedSlot.Value;
                PersistInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstOccupiedSlot.Value);
            }

            return true;
        }

        SeedStarterInventory(playerName, slots);
        return true;
    }

    private void RefreshAuthoritativePlayerCombatStats(PlayerSessionId sessionId)
    {
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? target)
            || player == null
            || target == null)
        {
            return;
        }

        target.Combatant.ApplyEquippedToolModifier(ResolveEquippedToolModifier(player.DisplayName));
    }

    private void SeedStarterInventory(string playerName, Dictionary<int, long> slots)
    {
        int slotIndex = 0;
        long? firstItemInstanceId = null;
        foreach (string itemKind in ToolCatalog.StarterItemKinds)
        {
            ToolDefinition definition = ToolCatalog.Resolve(itemKind);
            long itemInstanceId = _nextItemInstanceId++;
            firstItemInstanceId ??= itemInstanceId;
            PersistedItemInstance item = new(
                itemInstanceId,
                definition.ItemKind,
                definition.DisplayName,
                definition.VisualId,
                playerName,
                WorldPosition: null,
                State: "Inventory",
                StackCount: 1,
                CreatedUtc: DateTime.UtcNow,
                UpdatedUtc: DateTime.UtcNow);
            _itemInstances[item.ItemInstanceId] = item;
            slots[slotIndex] = itemInstanceId;
            EnqueueWorldWrite(world => world.SaveItemInstance(item));
            PersistInventorySlot(playerName, slotIndex, itemInstanceId);
            slotIndex++;
        }

        if (firstItemInstanceId.HasValue)
        {
            slots[InventoryConstants.EquippedSlotIndex] = firstItemInstanceId.Value;
            PersistInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstItemInstanceId.Value);
        }
    }

    private PlayerInventorySnapshot BuildInventorySnapshot(string playerName)
    {
        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        List<InventorySlotSnapshot> backpack = [];
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            InventoryItemSnapshot? item = TryResolveInventoryItem(slots, slotIndex);
            bool equipped = slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long equippedItemId)
                && item.HasValue
                && item.Value.ItemInstanceId == equippedItemId;
            backpack.Add(new InventorySlotSnapshot(slotIndex, item, equipped));
        }

        InventoryItemSnapshot? equippedItem = TryResolveInventoryItem(slots, InventoryConstants.EquippedSlotIndex);
        return new PlayerInventorySnapshot(
            playerName,
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            backpack,
            new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, equippedItem, equippedItem.HasValue));
    }

    private InventoryItemSnapshot? TryResolveInventoryItem(Dictionary<int, long> slots, int slotIndex)
    {
        if (!slots.TryGetValue(slotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return null;
        }

        ToolDefinition definition = ToolCatalog.Resolve(item.ItemKind);
        return new InventoryItemSnapshot(
            item.ItemInstanceId,
            item.ItemKind,
            item.DisplayName,
            item.VisualId,
            item.StackCount,
            definition.Description,
            definition.DetailLines);
    }

    private ToolStatModifier ResolveEquippedToolModifier(string playerName)
    {
        if (!_inventorySlotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return default;
        }

        return ToolCatalog.Resolve(item.ItemKind).StatModifier;
    }

    private Dictionary<int, long> GetOrCreateInventorySlots(string playerName)
    {
        if (!_inventorySlotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            slots = [];
            _inventorySlotsByPlayer[playerName] = slots;
        }

        return slots;
    }

    private void PersistInventorySlot(string playerName, int slotIndex, long itemInstanceId)
    {
        PersistedInventorySlot slot = new(
            playerName,
            slotIndex,
            itemInstanceId,
            DateTime.UtcNow);
        EnqueueWorldWrite(world => world.SaveInventorySlot(slot));
    }
}
