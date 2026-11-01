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
    bool CloseInventory = false)
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
    // Equip the backpack item in SlotIndex into the equipment slot index Target (100..103).
    EquipTo,
    // Empty the equipment slot SlotIndex (100..103). The server refuses any other index.
    Unequip,
    // Move or swap the backpack slot SlotIndex with the backpack slot Target.
    Move,
    // Destroy the item ItemInstanceId in the slot SlotIndex. The HUD asks for a confirm first.
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
