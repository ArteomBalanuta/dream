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
    BuildMaterialChoice? BuildMaterial = null)
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

// A building menu pick. MaterialId null is Auto, so the request wraps it to tell "Auto picked"
// from "nothing picked".
public readonly record struct BuildMaterialChoice(GeometryMaterialId? MaterialId);
