using Dream.Multiplayer.Protocol;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Simulation.Inventory;

// Ground items (M3 workstream A): the ground dictionary, the drop of rolled loot, the pickup and the
// despawn. Main-thread state like the rest of the service. A ground item is a persisted item row with
// state 'Ground', no owner and a position; its claim (who may pick it up first) lives in memory only,
// by character key, so a restart frees every claim and restarts every despawn timer.
internal sealed partial class PlayerInventoryService
{
    // The most items lying on the ground; the oldest drop is despawned to make room for a new one.
    internal const int MaxGroundItems = 256;

    // How far (metres) a player may be from an item to pick it up.
    internal const float GroundPickupRange = 4f;

    internal const int GroundClaimTicks = 30 * SimulationClock.AuthoritativeTicksPerSecond;

    internal const int GroundDespawnTicks = 300 * SimulationClock.AuthoritativeTicksPerSecond;

    private const string GroundState = "Ground";

    private sealed class GroundItem(
        PersistedItemInstance item,
        ulong dropOrder,
        string? claimantKey,
        int claimantActorId,
        ulong claimExpiresTick,
        ulong despawnTick)
    {
        public PersistedItemInstance Item { get; } = item;

        public ulong DropOrder { get; } = dropOrder;

        // Null once the claim has lapsed (or never existed): the item is free for all.
        public string? ClaimantKey { get; set; } = claimantKey;

        public int ClaimantActorId { get; set; } = claimantActorId;

        public ulong ClaimExpiresTick { get; set; } = claimExpiresTick;

        public ulong DespawnTick { get; } = despawnTick;
    }

    private readonly Dictionary<long, GroundItem> _groundItems = [];

    // Changes since the host last drained them: ids to (re)send and ids to remove. An id is in at most
    // one of them.
    private readonly HashSet<long> _groundUpserted = [];
    private readonly List<long> _groundRemoved = [];

    private ulong _nextGroundDropOrder;

    // The earliest tick a claim lapses or an item despawns, so a tick with nothing due only compares.
    private ulong _nextGroundEventTick = ulong.MaxValue;

    internal int GroundItemCount => _groundItems.Count;

    internal bool HasPendingGroundDeltas => _groundUpserted.Count > 0 || _groundRemoved.Count > 0;

    internal bool IsOnGround(long itemInstanceId) => _groundItems.ContainsKey(itemInstanceId);

    // Rolls the item, puts it on the ground at the position and persists it. The claimant (character
    // key and actor id) may pick it up alone until the claim lapses; a null key drops it free for all.
    // False (nothing changed) for a kind that is not a rollable item, a rarity out of range or a
    // non-finite position.
    internal bool TryDropRolled(
        string kind,
        ItemRarity rarity,
        in ItemRollSeed seed,
        Vector3 position,
        string? claimantKey,
        int claimantActorId,
        ulong tick)
    {
        if (!ContentCatalog.Shipped.TryGetItem(kind, out ItemDefinition? definition)
            || definition.Rolls == null
            || !Enum.IsDefined(rarity)
            || !IsFinite(position))
        {
            return false;
        }

        if (_groundItems.Count >= MaxGroundItems)
        {
            DespawnOldestGroundItem();
        }

        ToolDefinition tool = ToolCatalog.Resolve(definition.Key);
        long itemInstanceId = _objectIds.Take().Value;
        PersistedItemInstance item = new(
            itemInstanceId,
            tool.ItemKind,
            tool.DisplayName,
            tool.VisualId,
            OwnerPlayerName: null,
            WorldPosition: position,
            State: GroundState,
            StackCount: 1,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow,
            Roll: ItemRoller.Roll(definition, rarity, in seed));
        bool claimed = claimantKey != null;
        AddGroundItem(new GroundItem(
            item,
            _nextGroundDropOrder++,
            claimantKey,
            claimed ? claimantActorId : 0,
            claimed ? tick + GroundClaimTicks : 0UL,
            tick + GroundDespawnTicks));
        EnqueueWorldWrite(world => world.SaveItemInstance(item));
        return true;
    }

    // Loads the ground items a world restore found. Their claims are gone and their despawn timers start
    // now. Past the cap the newest are kept and the rest deleted. Rows that are not a placeable ground
    // item (no or a non-finite position, another state, an id already in use) are left alone.
    internal void RestoreGroundItems(IReadOnlyList<PersistedItemInstance> items, ulong tick)
    {
        List<PersistedItemInstance> usable = new(items.Count);
        foreach (PersistedItemInstance item in items)
        {
            if (item.WorldPosition is not { } position
                || !IsFinite(position)
                || !string.Equals(item.State, GroundState, StringComparison.Ordinal)
                || item.ItemInstanceId < 1)
            {
                continue;
            }

            _objectIds.Reserve(new ObjectId(item.ItemInstanceId));
            if (_groundItems.ContainsKey(item.ItemInstanceId) || _itemInstances.ContainsKey(item.ItemInstanceId))
            {
                continue;
            }

            usable.Add(item);
        }

        usable.Sort(static (a, b) => a.ItemInstanceId.CompareTo(b.ItemInstanceId));
        int skip = Math.Max(0, usable.Count - MaxGroundItems);
        for (int i = 0; i < usable.Count; i++)
        {
            PersistedItemInstance item = usable[i];
            if (i < skip)
            {
                long id = item.ItemInstanceId;
                EnqueueWorldWrite(world => world.DeleteGroundItem(id));
                continue;
            }

            AddGroundItem(new GroundItem(item, _nextGroundDropOrder++, null, 0, 0UL, tick + GroundDespawnTicks));
        }
    }

    // Appends every ground item to destination, in item id order (a client's Reset).
    internal void CaptureGroundItems(List<GroundItemSnapshot> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int start = destination.Count;
        foreach (GroundItem ground in _groundItems.Values)
        {
            destination.Add(ToSnapshot(ground));
        }

        destination.Sort(start, destination.Count - start, GroundSnapshotIdComparer.Instance);
    }

    private sealed class GroundSnapshotIdComparer : IComparer<GroundItemSnapshot>
    {
        public static readonly GroundSnapshotIdComparer Instance = new();

        public int Compare(GroundItemSnapshot x, GroundItemSnapshot y) => x.ItemInstanceId.CompareTo(y.ItemInstanceId);
    }

    // Moves the pending changes into the lists and clears them.
    internal void DrainGroundDeltas(List<GroundItemSnapshot> upserts, List<long> removedIds)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removedIds);
        int start = upserts.Count;
        foreach (long id in _groundUpserted)
        {
            if (_groundItems.TryGetValue(id, out GroundItem? ground))
            {
                upserts.Add(ToSnapshot(ground));
            }
        }

        upserts.Sort(start, upserts.Count - start, GroundSnapshotIdComparer.Instance);
        removedIds.AddRange(_groundRemoved);
        _groundUpserted.Clear();
        _groundRemoved.Clear();
    }

    // Picks the item up into the player's backpack. Order: exists, claim, range, bag fit, then the
    // action budget, so a rejection changes and queues nothing. The item leaves the ground and enters the
    // bag on the main thread, and one closure turns its row into an inventory item and writes its slot.
    internal InventoryEquipOutcome TryPickUpGround(string playerName, long itemInstanceId, Vector3 playerPosition, ulong tick)
    {
        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (!_groundItems.TryGetValue(itemInstanceId, out GroundItem? ground))
        {
            return InventoryEquipOutcome.ItemGone;
        }

        if (ground.ClaimantKey != null
            && tick < ground.ClaimExpiresTick
            && !string.Equals(ground.ClaimantKey, playerName, StringComparison.Ordinal))
        {
            return InventoryEquipOutcome.NotYours;
        }

        Vector3 at = ground.Item.WorldPosition ?? Vector3.Zero;
        if (!IsFinite(playerPosition) || (playerPosition - at).LengthSquared > GroundPickupRange * GroundPickupRange)
        {
            return InventoryEquipOutcome.TooFar;
        }

        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        int freeSlot = BuildBoard(slots).FirstFit(BagGrid.FootprintOf(ground.Item.ItemKind));
        if (freeSlot < 0)
        {
            return InventoryEquipOutcome.BackpackFull;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        RemoveGroundItem(ground);
        DateTime now = DateTime.UtcNow;
        PersistedItemInstance item = ground.Item with
        {
            OwnerPlayerName = playerName,
            WorldPosition = null,
            State = "Inventory",
            UpdatedUtc = now,
        };
        _itemInstances[item.ItemInstanceId] = item;
        slots[freeSlot] = item.ItemInstanceId;
        PersistedInventorySlot slot = new(playerName, freeSlot, item.ItemInstanceId, now);
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world =>
        {
            world.SaveItemInstance(item);
            world.SaveInventorySlot(slot);
        });
        return InventoryEquipOutcome.Equipped;
    }

    // Lapses claims and despawns items that have been on the ground too long. Compares one number when
    // nothing is due.
    internal void StepGroundItems(ulong tick)
    {
        if (tick < _nextGroundEventTick)
        {
            return;
        }

        List<GroundItem>? despawned = null;
        ulong next = ulong.MaxValue;
        foreach (GroundItem ground in _groundItems.Values)
        {
            if (tick >= ground.DespawnTick)
            {
                (despawned ??= []).Add(ground);
                continue;
            }

            if (ground.ClaimantKey != null)
            {
                if (tick >= ground.ClaimExpiresTick)
                {
                    ground.ClaimantKey = null;
                    ground.ClaimantActorId = 0;
                    ground.ClaimExpiresTick = 0;
                    _groundUpserted.Add(ground.Item.ItemInstanceId);
                }
                else
                {
                    next = Math.Min(next, ground.ClaimExpiresTick);
                }
            }

            next = Math.Min(next, ground.DespawnTick);
        }

        if (despawned != null)
        {
            foreach (GroundItem ground in despawned)
            {
                Despawn(ground);
            }
        }

        _nextGroundEventTick = next;
    }

    private void DespawnOldestGroundItem()
    {
        GroundItem? oldest = null;
        foreach (GroundItem ground in _groundItems.Values)
        {
            if (oldest == null || ground.DropOrder < oldest.DropOrder)
            {
                oldest = ground;
            }
        }

        if (oldest != null)
        {
            Despawn(oldest);
        }
    }

    // Removes from memory first, so a pickup that follows finds nothing, then deletes the row; the
    // delete is guarded by the state, so it cannot reach an item that has entered an inventory.
    private void Despawn(GroundItem ground)
    {
        RemoveGroundItem(ground);
        long id = ground.Item.ItemInstanceId;
        EnqueueWorldWrite(world => world.DeleteGroundItem(id));
    }

    private void AddGroundItem(GroundItem ground)
    {
        long id = ground.Item.ItemInstanceId;
        _groundItems[id] = ground;
        _groundUpserted.Add(id);
        ulong due = ground.DespawnTick;
        if (ground.ClaimantKey != null)
        {
            due = Math.Min(due, ground.ClaimExpiresTick);
        }

        _nextGroundEventTick = Math.Min(_nextGroundEventTick, due);
    }

    private void RemoveGroundItem(GroundItem ground)
    {
        long id = ground.Item.ItemInstanceId;
        _groundItems.Remove(id);
        _groundUpserted.Remove(id);
        _groundRemoved.Add(id);
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static GroundItemSnapshot ToSnapshot(GroundItem ground)
        => new(
            ground.Item.ItemInstanceId,
            ground.Item.ItemKind,
            ground.Item.Roll?.Rarity ?? ItemRarity.Common,
            ground.Item.WorldPosition ?? Vector3.Zero,
            ground.ClaimantKey != null ? ground.ClaimantActorId : 0,
            ground.ClaimantKey != null ? ground.ClaimExpiresTick : 0UL);
}
