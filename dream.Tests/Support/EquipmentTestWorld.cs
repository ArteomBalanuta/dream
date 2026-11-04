using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Tests.Support;

// An in-memory world repository for equipment tests: inventory rows, item rows (with their rolls),
// starter grants and player world states are kept, and the wave H deletes (DeleteInventorySlot,
// DestroyInventoryItem) are honoured. Every other member is inert. Counts the slot writes it sees
// so a test can pin "no write" and flood limits. Thread-safe enough for the inline write queue and
// a background worker that is flushed before reading.
public sealed class EquipmentTestWorld : IWorldPersistenceRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<long, PersistedItemInstance> _items = [];
    private readonly Dictionary<(string Player, int Slot), PersistedInventorySlot> _slots = [];
    private readonly Dictionary<string, int> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistedPlayerWorldState> _states = new(StringComparer.Ordinal);
    private int _slotWrites;

    // SaveInventorySlot, DeleteInventorySlot and DestroyInventoryItem calls so far.
    public int SlotWrites => Volatile.Read(ref _slotWrites);

    public void PutItem(PersistedItemInstance item)
    {
        lock (_gate)
        {
            _items[item.ItemInstanceId] = item;
        }
    }

    public void PutSlot(string player, int slotIndex, long itemInstanceId)
    {
        lock (_gate)
        {
            _slots[(player, slotIndex)] = new PersistedInventorySlot(player, slotIndex, itemInstanceId, DateTime.UtcNow);
        }
    }

    public void PutGrant(string player, int version)
    {
        lock (_gate)
        {
            _grants[player] = version;
        }
    }

    public Dictionary<int, long> SlotsOf(string player)
    {
        lock (_gate)
        {
            return _slots.Values.Where(slot => slot.PlayerName == player).ToDictionary(slot => slot.SlotIndex, slot => slot.ItemInstanceId);
        }
    }

    public bool HasItem(long itemInstanceId)
    {
        lock (_gate)
        {
            return _items.ContainsKey(itemInstanceId);
        }
    }

    public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName)
    {
        lock (_gate)
        {
            return _states.TryGetValue(playerName, out PersistedPlayerWorldState state) ? state : null;
        }
    }

    public void SavePlayerWorldState(PersistedPlayerWorldState state)
    {
        lock (_gate)
        {
            _states[state.PlayerName] = state;
        }
    }

    public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => [];

    public void SavePlacedCube(PersistedPlacedCube cube)
    {
    }

    public void DeletePlacedCube(ObjectId objectId)
    {
    }

    public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => [];

    public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece)
    {
    }

    public void DeleteCubeFracturePieces(ObjectId originObjectId)
    {
    }

    public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => [];

    public void SaveEnemyInstance(PersistedEnemyInstance enemy)
    {
    }

    public void DeleteEnemyInstance(ObjectId objectId)
    {
    }

    public long LoadNextObjectId()
    {
        lock (_gate)
        {
            long max = _items.Count == 0 ? 0 : _items.Keys.Max();
            return (_slots.Count == 0 ? max : Math.Max(max, _slots.Values.Max(slot => slot.ItemInstanceId))) + 1;
        }
    }

    public IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides() => [];

    public void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride)
    {
    }

    public IReadOnlyList<PersistedItemInstance> LoadItemInstances()
    {
        lock (_gate)
        {
            return _items.Values.ToArray();
        }
    }

    public void SaveItemInstance(PersistedItemInstance item)
    {
        lock (_gate)
        {
            // Like the real upsert, a roll is never changed by a later save.
            if (_items.TryGetValue(item.ItemInstanceId, out PersistedItemInstance existing))
            {
                item = item with { Roll = existing.Roll };
            }

            _items[item.ItemInstanceId] = item;
        }
    }

    public void DeleteItemInstance(long itemInstanceId)
    {
        lock (_gate)
        {
            _items.Remove(itemInstanceId);
        }
    }

    public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots()
    {
        lock (_gate)
        {
            return _slots.Values.ToArray();
        }
    }

    public PersistedPlayerInventory LoadPlayerInventory(string playerName)
    {
        lock (_gate)
        {
            string name = playerName.Trim();
            PersistedInventorySlot[] slots = _slots.Values
                .Where(slot => slot.PlayerName == name)
                .OrderBy(slot => slot.SlotIndex)
                .ToArray();
            PersistedItemInstance[] items = slots
                .Select(slot => slot.ItemInstanceId)
                .Distinct()
                .Where(_items.ContainsKey)
                .Select(id => _items[id])
                .ToArray();
            return new PersistedPlayerInventory(slots, items, _grants.TryGetValue(name, out int kit) ? kit : null);
        }
    }

    public void SaveInventorySlot(PersistedInventorySlot slot)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _slotWrites);
            _slots[(slot.PlayerName.Trim(), slot.SlotIndex)] = slot with { PlayerName = slot.PlayerName.Trim() };
        }
    }

    public void DeleteInventorySlots(string playerName)
    {
        lock (_gate)
        {
            foreach ((string Player, int Slot) key in _slots.Keys.Where(key => key.Player == playerName.Trim()).ToArray())
            {
                _slots.Remove(key);
            }
        }
    }

    public void DeleteInventorySlot(string playerName, int slotIndex, long expectedItemInstanceId)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _slotWrites);
            if (_slots.TryGetValue((playerName.Trim(), slotIndex), out PersistedInventorySlot row)
                && row.ItemInstanceId == expectedItemInstanceId)
            {
                _slots.Remove((playerName.Trim(), slotIndex));
            }
        }
    }

    public void DestroyInventoryItem(string playerName, long itemInstanceId)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _slotWrites);
            string name = playerName.Trim();
            foreach ((string Player, int Slot) key in _slots
                .Where(pair => pair.Key.Player == name && pair.Value.ItemInstanceId == itemInstanceId)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _slots.Remove(key);
            }

            if (!_slots.Values.Any(slot => slot.ItemInstanceId == itemInstanceId))
            {
                _items.Remove(itemInstanceId);
            }
        }
    }

    public void SaveStarterKitGrant(string playerName, int kitVersion)
    {
        lock (_gate)
        {
            string name = playerName.Trim();
            _grants[name] = Math.Max(_grants.GetValueOrDefault(name), kitVersion);
        }
    }

    // An item of the kind owned by the player, optionally rolled.
    public static PersistedItemInstance MakeItem(long id, string owner, string kind, ItemRoll? roll = null)
    {
        ToolDefinition definition = ToolCatalog.Resolve(kind);
        return new PersistedItemInstance(
            id,
            definition.ItemKind,
            definition.DisplayName,
            definition.VisualId,
            owner,
            WorldPosition: null,
            State: "Inventory",
            StackCount: 1,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow,
            Roll: roll);
    }
}

public sealed class EquipmentTestProfiles : IPlayerProfileRepository
{
    private readonly Dictionary<string, PersistedPlayerProfile> _profiles = new(StringComparer.Ordinal);

    public PersistedPlayerProfile GetOrCreate(string playerName)
    {
        if (!_profiles.TryGetValue(playerName.Trim(), out PersistedPlayerProfile profile))
        {
            // The baseline, not the shipping default: a new character is created at the attribute
            // ceiling, where vitality gear cannot raise max health and there is nothing to spend,
            // and the tests on this double are about exactly those mechanics.
            profile = TestStatBaseline.Profile(playerName.Trim(), DateTime.UtcNow);
            _profiles[profile.PlayerName] = profile;
        }

        return profile;
    }

    public PersistedPlayerProfile? TryGet(string playerName)
        => _profiles.TryGetValue(playerName.Trim(), out PersistedPlayerProfile profile) ? profile : null;

    public void Save(PersistedPlayerProfile profile) => _profiles[profile.PlayerName] = profile;
}
