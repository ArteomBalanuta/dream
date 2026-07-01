using OpenTK.Mathematics;

namespace Dream.Rendering.ViewModels;


public sealed class ViewModelRigSettings
{
    public float FieldOfViewDegrees { get; set; } = 60f;

    public Vector3 PositionOffset { get; set; } = Vector3.Zero;

    public Vector3 RotationOffsetDegrees { get; set; } = Vector3.Zero;

    public ViewModelSlotTuning Hands { get; set; } = new();

    public ViewModelSlotTuning HeldItem { get; set; } = new()
    {
        PositionOffset = new Vector3(-0.046f, 0.204f, -0.156f),
        RotationOffsetDegrees = new Vector3(0.938f, 1.406f, -1.406f)
    };

    public ViewModelBodyAnchorSettings BodyAnchor { get; set; } = new();

    public ViewModelRigSettings Clone()
    {
        return new ViewModelRigSettings
        {
            FieldOfViewDegrees = FieldOfViewDegrees,
            PositionOffset = PositionOffset,
            RotationOffsetDegrees = RotationOffsetDegrees,
            Hands = new ViewModelSlotTuning
            {
                PositionOffset = Hands.PositionOffset,
                RotationOffsetDegrees = Hands.RotationOffsetDegrees
            },
            HeldItem = new ViewModelSlotTuning
            {
                PositionOffset = HeldItem.PositionOffset,
                RotationOffsetDegrees = HeldItem.RotationOffsetDegrees
            },
            BodyAnchor = new ViewModelBodyAnchorSettings
            {
                HorizontalSpread = BodyAnchor.HorizontalSpread,
                VerticalDrop = BodyAnchor.VerticalDrop,
                ForwardPull = BodyAnchor.ForwardPull
            }
        };
    }

    public static ViewModelRigSettings CreateDefault()
    {
        return new ViewModelRigSettings
        {
            HeldItem = new ViewModelSlotTuning
            {
                PositionOffset = new Vector3(-0.046f, 0.204f, -0.156f),
                RotationOffsetDegrees = new Vector3(0.938f, 1.406f, -1.406f)
            },
            BodyAnchor = new ViewModelBodyAnchorSettings
            {
                HorizontalSpread = 0.000f,
                VerticalDrop = 0.306f,
                ForwardPull = 0.180f
            }
        };
    }
}
