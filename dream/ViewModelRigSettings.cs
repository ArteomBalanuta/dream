using OpenTK.Mathematics;

public sealed class ViewModelRigSettings
{
    public float FieldOfViewDegrees { get; set; } = 60f;

    public Vector3 PositionOffset { get; set; } = Vector3.Zero;

    public Vector3 RotationOffsetDegrees { get; set; } = Vector3.Zero;

    public ViewModelSlotTuning Hands { get; set; } = new();

    public ViewModelSlotTuning HeldItem { get; set; } = new();

    public ViewModelBodyAnchorSettings BodyAnchor { get; set; } = new();

    public static ViewModelRigSettings CreateDefault() => new();
}
