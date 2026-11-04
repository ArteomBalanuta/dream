using OpenTK.Mathematics;
using Dream.Application;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.Hud;


public readonly record struct HudInteractionRequest(
    float? FieldOfViewDegrees,
    float? MouseSensitivity,
    CameraViewMode? CameraViewMode,
    float? NoclipSpeed,
    Vector2i? RequestedResolution,
    int? StagedFloorSize,
    int? RequestedFloorSize,
    bool? ShowCollisionGeometry,
    bool? ShowPerformanceCounters,
    int? Strength,
    float? AttackSpeed,
    int? Vitality,
    FloatingCombatTextTuning? FloatingCombatText,
    int? EquipInventorySlotIndex,
    BuildMaterialChoice? BuildMaterial = null,
    InventoryHudAction? InventoryAction = null,
    bool CloseInventory = false,
    // A UI scale picked in Settings: 0 is auto, other values snap to quarter steps (GameSettings.SetUiScale).
    float? UiScale = null,
    // The slider was released: only then is the scale written to the config file.
    bool UiScaleCommitted = false,
    // M3: a ground item to pick up (a clicked label), an NPC to talk to, a vendor window pick, and the
    // vendor window's close button. The scene turns each into one intent.
    long? GroundPickupId = null,
    int? InteractActorId = null,
    VendorHudAction? Vendor = null,
    bool CloseVendor = false,
    // M4: a quickbar tile clicked while the inventory is open (0..5). The scene sends the next entry in
    // the cycle as one QuickbarBindIntent.
    int? QuickbarCycleSlot = null)
{
    public static HudInteractionRequest None => new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}

public enum InventoryHudActionKind : byte
{
    // Equip the bag item anchored at SlotIndex (0..63) into the equipment slot index Target (100..111).
    // Also a move between two wells: SlotIndex is the source well (100..111), Target the other well; the
    // server swaps when the target holds an item.
    EquipTo,
    // Empty the equipment slot SlotIndex (100..111). Target is the bag anchor (0..63) the item should
    // land on, or -1 for the server's first fit; it travels as InventoryUnequipIntent.TargetCell (null
    // for -1). Target defaults to 0, which is the cell 0 anchor, so a caller with no cell must pass -1.
    // The server refuses any other SlotIndex.
    Unequip,
    // Move the bag item anchored at SlotIndex (0..63) so its top-left cell lands on the anchor Target
    // (0..63). Over exactly one other item the two swap (BagGrid.PlanMove). A SlotIndex that is not the
    // anchor of an item is refused.
    Move,
    // Destroy the item ItemInstanceId anchored at SlotIndex (0..63). The HUD asks for a confirm first.
    Destroy,
    // Spend Target stat points on the stat whose StatId is SlotIndex (Strength, Vitality or Attack
    // Speed). It travels as PlayerStatSpendIntent; the server checks the budget and the caps.
    SpendStat,
    // Use (drink) the consumable ItemInstanceId anchored at SlotIndex (0..63). It travels as
    // ItemUseIntent; the server checks the item, the cooldown and that something would be restored.
    Use,
    // Drop the bag item ItemInstanceId anchored at SlotIndex (0..63) onto the ground at the player's
    // own position. It travels as InventoryDropIntent; no confirm, because the item stays in the world
    // and the dropper can pick it straight back up, unlike Destroy.
    Drop
}

// One inventory action picked in the character panel. Only slot numbers and the id of an item the
// panel is showing; the server checks every one of them.
public readonly record struct InventoryHudAction(
    InventoryHudActionKind Kind,
    int SlotIndex,
    int Target = 0,
    long ItemInstanceId = 0);

public enum VendorHudActionKind : byte
{
    // Buy one of ItemKind (a kind the open vendor sells).
    Buy,
    // Sell the bag item ItemInstanceId anchored at SlotIndex (0..63).
    Sell
}

// One pick in the vendor window. Names only what the window showed; the scene adds the NPC it is open
// for, and the server checks the NPC, the range, the stock, the slot and the gold.
public readonly record struct VendorHudAction(
    VendorHudActionKind Kind,
    string? ItemKind = null,
    int SlotIndex = 0,
    long ItemInstanceId = 0);

// The economy picks of one classic frame (M3): a ground label or the vendor window. All empty is the
// common case.
public readonly record struct HudEconomyRequest(
    long? GroundPickupId = null,
    int? InteractActorId = null,
    VendorHudAction? Vendor = null,
    bool CloseVendor = false)
{
    public bool IsEmpty => GroundPickupId is null && InteractActorId is null && Vendor is null && !CloseVendor;
}

// A building menu pick. MaterialId null is Auto, so the request wraps it to tell "Auto picked"
// from "nothing picked".
public readonly record struct BuildMaterialChoice(GeometryMaterialId? MaterialId);
