using OpenTK.Mathematics;
using Dream.Application;

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
    int? EquipInventorySlotIndex)
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
