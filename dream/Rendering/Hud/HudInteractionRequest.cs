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
    // The attribute whose "+" was pressed in the character window; sent as a PlayerAttributeSpendIntent.
    Dream.Simulation.Stats.StatId? SpendAttribute = null)
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
    Destroy
}

// One inventory action picked in the character panel. Only slot numbers and the id of an item the
// panel is showing; the server checks every one of them.
public readonly record struct InventoryHudAction(
    InventoryHudActionKind Kind,
    int SlotIndex,
    int Target = 0,
    long ItemInstanceId = 0);

// A building menu pick. MaterialId null is Auto, so the request wraps it to tell "Auto picked"
// from "nothing picked".
public readonly record struct BuildMaterialChoice(GeometryMaterialId? MaterialId);
