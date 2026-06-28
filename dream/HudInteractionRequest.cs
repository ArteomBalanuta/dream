using OpenTK.Mathematics;

public readonly record struct HudInteractionRequest(
    float? FieldOfViewDegrees,
    float? MouseSensitivity,
    Vector2i? RequestedResolution,
    int? StagedFloorSize,
    int? RequestedFloorSize,
    bool? ShowCollisionGeometry)
{
    public static HudInteractionRequest None => new(
        null,
        null,
        null,
        null,
        null,
        null);
}
