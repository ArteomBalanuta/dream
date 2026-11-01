using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Ui;

// Refills one reused HudView. The rows (stats, equipment, backpack, worn) are rebuilt only when the
// status or the inventory snapshot changed; an unchanged frame allocates nothing. A hostile or
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

    // Returns true when the rows were rebuilt (the inventory or the status changed).
    public bool Refresh(in HudStatusSnapshot status, PlayerInventorySnapshot? inventory)
    {
        HudView view = View;
        view.Status = status;
        view.Inventory = inventory;
        if (_haveRows && _status.Equals(status) && SameInventory(_inventory, inventory))
        {
            return false;
        }

        _haveRows = true;
        _status = status;
        _inventory = inventory;
        view.StatRows = CharacterPanelModel.BuildStatRows(status);
        view.Equipment = CharacterPanelModel.BuildSlotRows(inventory);
        FillBackpack(view.Backpack, inventory);
        FillWorn(view.Worn, view.Equipment);
        return true;
    }

    // Typed Equals on the record struct: Nullable.Equals would box.
    private static bool SameInventory(PlayerInventorySnapshot? a, PlayerInventorySnapshot? b)
    {
        if (a is not PlayerInventorySnapshot left || b is not PlayerInventorySnapshot right)
        {
            return a is null && b is null;
        }

        return left.Equals(right);
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
