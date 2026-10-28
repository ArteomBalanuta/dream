using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Runtime;

// Inventory: inventory snapshots, equip actions, starter inventory, slot persistence and
// equipped-tool combat stats.
public sealed partial class WorldSessionRuntime
{
    private readonly Dictionary<long, PersistedItemInstance> _itemInstances = [];
    private readonly Dictionary<string, Dictionary<int, long>> _inventorySlotsByPlayer = new(StringComparer.OrdinalIgnoreCase);

    private long _nextItemInstanceId = 1;

    public PlayerInventorySnapshot? TryCaptureInventorySnapshot(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) || player == null)
        {
            return null;
        }

        EnsurePlayerInventory(player.DisplayName);
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

        EnsurePlayerInventory(player.DisplayName);
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

    private void RestorePersistedInventoryState()
    {
        if (_worldPersistence == null)
        {
            return;
        }

        foreach (PersistedItemInstance item in _worldPersistence.LoadItemInstances())
        {
            _itemInstances[item.ItemInstanceId] = item;
            _nextItemInstanceId = Math.Max(_nextItemInstanceId, item.ItemInstanceId + 1);
        }

        foreach (PersistedInventorySlot slot in _worldPersistence.LoadInventorySlots())
        {
            GetOrCreateInventorySlots(slot.PlayerName)[slot.SlotIndex] = slot.ItemInstanceId;
        }
    }

    private void EnsurePlayerInventory(string playerName)
    {
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

            return;
        }

        SeedStarterInventory(playerName, slots);
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
