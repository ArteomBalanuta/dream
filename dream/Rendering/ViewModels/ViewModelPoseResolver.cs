using OpenTK.Mathematics;

namespace Dream.Rendering.ViewModels;


public static class ViewModelPoseResolver
{
    public static ViewModelResolvedPose Resolve(ViewModelDefinition definition, ViewModelRigSettings rig)
    {
        ViewModelSlotTuning slotTuning = definition.Slot == ViewModelSlot.Hands
            ? rig.Hands
            : rig.HeldItem;
        // The off hand reuses the held-item tuning, mirrored (the definition is mirrored already).
        Vector3 tuningPosition = slotTuning.PositionOffset;
        Vector3 tuningRotation = slotTuning.RotationOffsetDegrees;
        if (definition.Slot == ViewModelSlot.OffHand)
        {
            tuningPosition = new Vector3(-tuningPosition.X, tuningPosition.Y, tuningPosition.Z);
            tuningRotation = new Vector3(tuningRotation.X, -tuningRotation.Y, -tuningRotation.Z);
        }

        Vector3 position =
            definition.PositionOffset +
            rig.PositionOffset +
            GetBodyAnchor(definition.Slot, rig.BodyAnchor) +
            tuningPosition;

        Vector3 rotation =
            definition.RotationOffsetDegrees +
            rig.RotationOffsetDegrees +
            tuningRotation;

        return new ViewModelResolvedPose(
            rig.FieldOfViewDegrees,
            position,
            rotation,
            definition.Scale);
    }

    private static Vector3 GetBodyAnchor(ViewModelSlot slot, ViewModelBodyAnchorSettings anchor)
    {
        float horizontal = slot is ViewModelSlot.Hands or ViewModelSlot.OffHand ? -anchor.HorizontalSpread : anchor.HorizontalSpread;
        return new Vector3(horizontal, -anchor.VerticalDrop, -anchor.ForwardPull);
    }
}
