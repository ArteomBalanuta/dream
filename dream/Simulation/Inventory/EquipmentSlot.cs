namespace Dream.Simulation.Inventory;

// The twelve equipment slots a player has. The numeric value is the equipment source slot of the
// player's StatSheet and must not change: sources resolve in ascending order. 0..3 are the original
// four (main hand, off hand, head, body); 4..11 were appended and fit the sheet's 16 sources.
// The two ring slots take the same item kind, so their stats stack (separate sources).
public enum EquipmentSlot : byte
{
    MainHand = 0,
    OffHand = 1,
    Head = 2,
    Body = 3,
    Hands = 4,
    Feet = 5,
    Legs = 6,
    Neck = 7,
    RingLeft = 8,
    RingRight = 9,
    Back = 10,
    Companion = 11,
}

// Equipment slot indices live beside the bag's anchors (0..63) in player_inventory_slots: 100 + the slot.
public static class EquipmentSlots
{
    public const int Count = 12;
    public const int FirstSlotIndex = InventoryConstants.EquippedSlotIndex;
    public const int LastSlotIndex = FirstSlotIndex + Count - 1;

    public static int ToSlotIndex(EquipmentSlot slot) => FirstSlotIndex + (int)slot;

    // False for any index that is not 100..111; wire data is never cast to the enum unchecked.
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
