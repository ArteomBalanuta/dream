using Dream.Simulation.Content;
using Dream.Simulation.Persistence;

namespace Dream.Simulation.Inventory;

// Quickbar and consumables (M4 workstream A). The quickbar is per character: six slots, each empty, a
// skill key or an item kind. A character that never bound anything has no entry (BuildSnapshot sends
// null and the client shows ContentCatalog.DefaultQuickbar); the first bind materialises the default into
// all six slots and saves them in one closure, so later binds save one row. A None slot is a stored row,
// so an all-empty bar does not fall back to the default. Retention follows the wallet: Evict, the
// failed-write restore and the moved-out tracker all carry it.
internal sealed partial class PlayerInventoryService
{
    private readonly Dictionary<string, QuickbarSlotSnapshot[]> _quickbarByPlayer = new(StringComparer.Ordinal);

    // Installs the loaded rows. No rows (or none valid) means no entry. A row with a slot out of range, an
    // unknown kind or text over the limit is skipped; later rows of one slot win.
    private void LoadQuickbar(string playerName, IReadOnlyList<PersistedQuickbarSlot>? rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        QuickbarSlotSnapshot[]? bar = null;
        for (int i = 0; i < rows.Count; i++)
        {
            PersistedQuickbarSlot row = rows[i];
            if (row.Slot < 0
                || row.Slot >= InventoryConstants.QuickbarSlots
                || row.Kind < (int)QuickbarEntryKind.None
                || row.Kind > (int)QuickbarEntryKind.Item
                || (row.Key?.Length ?? 0) > 256)
            {
                continue;
            }

            bar ??= NewEmptyBar();
            bar[row.Slot] = new QuickbarSlotSnapshot(row.Slot, (QuickbarEntryKind)row.Kind, row.Key ?? string.Empty);
        }

        if (bar != null)
        {
            _quickbarByPlayer[playerName] = bar;
        }
    }

    private static QuickbarSlotSnapshot[] NewEmptyBar()
    {
        QuickbarSlotSnapshot[] bar = new QuickbarSlotSnapshot[InventoryConstants.QuickbarSlots];
        for (int i = 0; i < bar.Length; i++)
        {
            bar[i] = new QuickbarSlotSnapshot(i, QuickbarEntryKind.None, string.Empty);
        }

        return bar;
    }

    // A copy, or null when this character never bound a slot.
    private IReadOnlyList<QuickbarSlotSnapshot>? BuildQuickbarSnapshot(string playerName)
        => _quickbarByPlayer.TryGetValue(playerName, out QuickbarSlotSnapshot[]? bar)
            ? (QuickbarSlotSnapshot[])bar.Clone()
            : null;

    // The bar the character effectively has: its own, or the content default.
    internal IReadOnlyList<QuickbarSlotSnapshot> EffectiveQuickbarOf(string playerName)
        => _quickbarByPlayer.TryGetValue(playerName, out QuickbarSlotSnapshot[]? bar)
            ? bar
            : ContentCatalog.Shipped.DefaultQuickbar;

    // Binds one slot. SlotOutOfRange for a slot outside 0..5; NotEquippable for an entry the content does
    // not know (None must carry an empty key, a Skill must resolve, an Item must resolve and be a
    // consumable); StillLoading until the inventory is loaded; Throttled when the budget is spent.
    // Binding what the slot already holds changes and spends nothing.
    internal InventoryEquipOutcome TryBindQuickbar(string playerName, int slot, QuickbarEntryKind kind, string key)
    {
        if (slot < 0 || slot >= InventoryConstants.QuickbarSlots)
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        key ??= string.Empty;
        string storedKey;
        switch (kind)
        {
            case QuickbarEntryKind.None:
                if (key.Length != 0)
                {
                    return InventoryEquipOutcome.NotEquippable;
                }

                storedKey = string.Empty;
                break;
            case QuickbarEntryKind.Skill:
                if (!ContentCatalog.Shipped.TryGetSkill(key, out SkillDefinition? skill))
                {
                    return InventoryEquipOutcome.NotEquippable;
                }

                storedKey = skill.Key;
                break;
            case QuickbarEntryKind.Item:
                if (!ContentCatalog.Shipped.TryGetItem(key, out ItemDefinition? item) || item.Use == null)
                {
                    return InventoryEquipOutcome.NotEquippable;
                }

                storedKey = item.Key;
                break;
            default:
                return InventoryEquipOutcome.NotEquippable;
        }

        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        bool hasBar = _quickbarByPlayer.TryGetValue(playerName, out QuickbarSlotSnapshot[]? bar);
        if (hasBar && bar![slot].Kind == kind && string.Equals(bar[slot].Key, storedKey, StringComparison.Ordinal))
        {
            return InventoryEquipOutcome.Equipped;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        DateTime now = DateTime.UtcNow;
        if (!hasBar)
        {
            bar = NewEmptyBar();
            IReadOnlyList<QuickbarSlotSnapshot> defaults = ContentCatalog.Shipped.DefaultQuickbar;
            for (int i = 0; i < bar.Length && i < defaults.Count; i++)
            {
                bar[i] = new QuickbarSlotSnapshot(i, defaults[i].Kind, defaults[i].Key ?? string.Empty);
            }

            bar[slot] = new QuickbarSlotSnapshot(slot, kind, storedKey);
            _quickbarByPlayer[playerName] = bar;
            PersistedQuickbarSlot[] rows = new PersistedQuickbarSlot[bar.Length];
            for (int i = 0; i < bar.Length; i++)
            {
                rows[i] = new PersistedQuickbarSlot(playerName, i, (int)bar[i].Kind, bar[i].Key, now);
            }

            NoteWriteQueued(playerName);
            EnqueueWorldWrite(world =>
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    world.SaveQuickbarSlot(rows[i]);
                }
            });
            return InventoryEquipOutcome.Equipped;
        }

        bar![slot] = new QuickbarSlotSnapshot(slot, kind, storedKey);
        PersistedQuickbarSlot row = new(playerName, slot, (int)kind, storedKey, now);
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world => world.SaveQuickbarSlot(row));
        return InventoryEquipOutcome.Equipped;
    }

    // Looks up the consumable anchored at a bag cell without changing anything: the same checks
    // TryDestroy makes (bag anchor, loaded, item there, the id matches, not worn), then NotEquippable
    // when the item's kind has no use definition. Spends no budget. Equipped means found.
    internal InventoryEquipOutcome TryPeekConsumable(
        string playerName,
        int slotIndex,
        long itemInstanceId,
        out ItemUseDefinition use,
        out string kind)
    {
        use = null!;
        kind = string.Empty;
        if (!InventoryConstants.IsBagAnchor(slotIndex))
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !TryGetItem(slots, slotIndex, out PersistedItemInstance item))
        {
            return InventoryEquipOutcome.SlotEmpty;
        }

        if (item.ItemInstanceId != itemInstanceId)
        {
            return InventoryEquipOutcome.ItemMismatch;
        }

        if (FindEquipmentSlotOf(slots, itemInstanceId) >= 0)
        {
            return InventoryEquipOutcome.ItemEquipped;
        }

        if (!ContentCatalog.Shipped.TryGetItem(item.ItemKind, out ItemDefinition? definition) || definition.Use == null)
        {
            return InventoryEquipOutcome.NotEquippable;
        }

        use = definition.Use;
        kind = definition.Key;
        return InventoryEquipOutcome.Equipped;
    }

    // Removes the consumable the peek found: the same removal as TryDestroy, including the action budget
    // (checked last, so a throttled use changes nothing) and the held-back drain.
    internal InventoryEquipOutcome TryConsume(string playerName, int slotIndex, long itemInstanceId)
    {
        InventoryEquipOutcome peek = TryPeekConsumable(playerName, slotIndex, itemInstanceId, out _, out _);
        if (peek != InventoryEquipOutcome.Equipped)
        {
            return peek;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        RemoveItem(playerName, _slotsByPlayer[playerName], itemInstanceId);
        return InventoryEquipOutcome.Equipped;
    }
}
