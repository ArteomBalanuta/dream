using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Ui;

// Refills one reused HudView. The stat rows are rebuilt only when the status changed, and the item
// rows (equipment, backpack, worn) only when the inventory snapshot changed, so a health tick does
// not rebuild the items; an unchanged frame allocates nothing. A hostile or
// partial snapshot never throws: bad slot indices are skipped, missing lists read as empty and an
// unknown item kind just has no icon.
public sealed class HudViewBuilder
{
    private readonly HudItemCatalog _catalog;
    private bool _haveRows;
    private HudStatusSnapshot _status;
    private PlayerInventorySnapshot? _inventory;

    public HudViewBuilder(UiSpriteTable sprites, HudView view)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(view);
        _catalog = new HudItemCatalog(sprites);
        View = view;
    }

    public HudView View { get; }

    // True when the last Refresh saw a different inventory snapshot from the one before. A status
    // change alone (a health tick) leaves it false, so it does not cancel a drag.
    public bool InventoryChanged { get; private set; }

    // Returns true when the rows were rebuilt (the inventory or the status changed).
    public bool Refresh(in HudStatusSnapshot status, PlayerInventorySnapshot? inventory)
    {
        HudView view = View;
        view.Status = status;
        bool sameInventory = SameInventory(_inventory, inventory);
        // A snapshot that reads the same as the last one is the old object as far as the panels go:
        // the server pushes a fresh copy for events that changed nothing in the bag, and a drag in
        // progress must survive that.
        view.Inventory = sameInventory && _haveRows ? _inventory : inventory;
        InventoryChanged = _haveRows && !sameInventory;
        bool statusChanged = !_haveRows || !_status.Equals(status);
        bool itemsChanged = !_haveRows || !sameInventory;
        if (!statusChanged && !itemsChanged)
        {
            return false;
        }

        _haveRows = true;
        if (statusChanged)
        {
            _status = status;
            view.StatRows = CharacterPanelModel.BuildStatRows(status);
            StatRowRebuilds++;
        }

        if (itemsChanged)
        {
            _inventory = view.Inventory;
            view.Equipment = CharacterPanelModel.BuildSlotRows(inventory);
            FillBackpack(view.Backpack, inventory);
            FillWorn(view.Worn, view.Equipment);
            ItemRowRebuilds++;
        }

        return true;
    }

    // How often each half was rebuilt (for tests).
    public int StatRowRebuilds { get; private set; }

    public int ItemRowRebuilds { get; private set; }

    // Same contents, not the same list objects: every decoded snapshot has fresh lists, and the
    // record's own Equals would call them all different.
    private static bool SameInventory(PlayerInventorySnapshot? a, PlayerInventorySnapshot? b)
    {
        if (a is not PlayerInventorySnapshot left || b is not PlayerInventorySnapshot right)
        {
            return a is null && b is null;
        }

        return string.Equals(left.PlayerName, right.PlayerName, StringComparison.Ordinal)
            && left.Columns == right.Columns
            && left.Rows == right.Rows
            && SameSlots(left.Slots, right.Slots)
            && SameSlot(left.EquippedSlot, right.EquippedSlot)
            && SameSlots(left.Equipment, right.Equipment);
    }

    private static bool SameSlots(IReadOnlyList<InventorySlotSnapshot>? a, IReadOnlyList<InventorySlotSnapshot>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!SameSlot(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameSlot(InventorySlotSnapshot? a, InventorySlotSnapshot? b)
    {
        if (a is not InventorySlotSnapshot left || b is not InventorySlotSnapshot right)
        {
            return a is null && b is null;
        }

        return left.SlotIndex == right.SlotIndex && left.IsEquipped == right.IsEquipped && SameItem(left.Item, right.Item);
    }

    private static bool SameItem(InventoryItemSnapshot? a, InventoryItemSnapshot? b)
    {
        if (a is not InventoryItemSnapshot left || b is not InventoryItemSnapshot right)
        {
            return a is null && b is null;
        }

        return left.ItemInstanceId == right.ItemInstanceId
            && left.StackCount == right.StackCount
            && left.Rarity == right.Rarity
            && left.IsRolled == right.IsRolled
            && left.EquipSlots == right.EquipSlots
            && left.HeldLight == right.HeldLight
            && string.Equals(left.ItemKind, right.ItemKind, StringComparison.Ordinal)
            && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
            && string.Equals(left.VisualId, right.VisualId, StringComparison.Ordinal)
            && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
            && SameStrings(left.DetailLines, right.DetailLines)
            && SameModifiers(left.Modifiers, right.Modifiers);
    }

    private static bool SameStrings(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameModifiers(IReadOnlyList<Simulation.Stats.StatModifier>? a, IReadOnlyList<Simulation.Stats.StatModifier>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void FillBackpack(HudItemView[] backpack, PlayerInventorySnapshot? inventory)
    {
        Array.Clear(backpack);
        IReadOnlyList<InventorySlotSnapshot>? slots = inventory?.Slots;
        if (slots is null)
        {
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            InventorySlotSnapshot slot = slots[i];
            if ((uint)slot.SlotIndex >= (uint)backpack.Length || slot.Item is not InventoryItemSnapshot item)
            {
                continue;
            }

            if (backpack[slot.SlotIndex].Item.ItemInstanceId != 0)
            {
                continue;
            }

            backpack[slot.SlotIndex] = _catalog.Describe(item, slot.IsEquipped);
        }
    }

    private void FillWorn(HudItemView[] worn, CharacterSlotRow[] equipment)
    {
        Array.Clear(worn);
        int count = Math.Min(worn.Length, equipment.Length);
        for (int i = 0; i < count; i++)
        {
            if (equipment[i].Item is InventoryItemSnapshot item)
            {
                worn[i] = _catalog.Describe(item, equipped: true);
            }
        }
    }
}
