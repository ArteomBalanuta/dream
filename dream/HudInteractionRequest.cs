using OpenTK.Mathematics;

public readonly record struct HudInteractionRequest(
    float? FieldOfViewDegrees,
    float? MouseSensitivity,
    float? NoclipSpeed,
    Vector2i? RequestedResolution,
    int? StagedFloorSize,
    int? RequestedFloorSize,
    bool? ShowCollisionGeometry,
    int? Strength,
    float? AttackSpeed,
    int? Vitality)
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
        null);
}
