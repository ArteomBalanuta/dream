using OpenTK.Mathematics;

namespace Dream.Rendering.ViewModels;


public static class ViewModelPoseResolver
{
    public static ViewModelResolvedPose Resolve(ViewModelDefinition definition, ViewModelRigSettings rig)
    {
        ViewModelSlotTuning slotTuning = definition.Slot == ViewModelSlot.Hands
            ? rig.Hands
            : rig.HeldItem;

        Vector3 position =
            definition.PositionOffset +
            rig.PositionOffset +
            GetBodyAnchor(definition.Slot, rig.BodyAnchor) +
            slotTuning.PositionOffset;

        Vector3 rotation =
            definition.RotationOffsetDegrees +
            rig.RotationOffsetDegrees +
            slotTuning.RotationOffsetDegrees;

        return new ViewModelResolvedPose(
            rig.FieldOfViewDegrees,
            position,
            rotation,
            definition.Scale);
    }

    private static Vector3 GetBodyAnchor(ViewModelSlot slot, ViewModelBodyAnchorSettings anchor)
    {
        float horizontal = slot == ViewModelSlot.Hands ? -anchor.HorizontalSpread : anchor.HorizontalSpread;
        return new Vector3(horizontal, -anchor.VerticalDrop, -anchor.ForwardPull);
    }
}
