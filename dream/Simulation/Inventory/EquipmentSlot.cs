namespace Dream.Simulation.Inventory;

// The four equipment slots a player has. The numeric value is the equipment source slot of the
// player's StatSheet (0 main, 1 off, 2 head, 3 body) and must not change: sources resolve in
// ascending order.
public enum EquipmentSlot : byte
{
    MainHand = 0,
    OffHand = 1,
    Head = 2,
    Body = 3,
}

// Equipment slot indices live beside the backpack's 0..15 in player_inventory_slots: 100 + the slot.
public static class EquipmentSlots
{
    public const int Count = 4;
    public const int FirstSlotIndex = InventoryConstants.EquippedSlotIndex;
    public const int LastSlotIndex = FirstSlotIndex + Count - 1;

    public static int ToSlotIndex(EquipmentSlot slot) => FirstSlotIndex + (int)slot;

    // False for any index that is not 100..103; wire data is never cast to the enum unchecked.
    public static bool TryFromSlotIndex(int slotIndex, out EquipmentSlot slot)
    {
        if (slotIndex >= FirstSlotIndex && slotIndex <= LastSlotIndex)
        {
            slot = (EquipmentSlot)(slotIndex - FirstSlotIndex);
            return true;
        }

        slot = EquipmentSlot.MainHand;
        return false;
    }

    public static int MaskOf(EquipmentSlot slot) => 1 << (int)slot;

    // One light per player: the off-hand item's light if it has one, otherwise the main hand's.
    public static HeldLightKind ResolveHeldLight(HeldLightKind mainHand, HeldLightKind offHand)
        => offHand != HeldLightKind.None ? offHand : mainHand;
}
