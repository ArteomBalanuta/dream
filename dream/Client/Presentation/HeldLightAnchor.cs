using OpenTK.Mathematics;

namespace Dream.Client.Presentation;

// Where a held light sits in the world (wave T2). First person anchors to the camera so the light follows the view.
// Third person and remote players ride the flame of the torch in the animated hand (HeldWeaponRenderer.
// TryGetFlameModelPoint); Body is only the fallback for a frame with no recorded flame point, so it sits where an
// idle off-hand torch head is: hand height and on the figure's LEFT, so a fallback frame does not throw the fire
// across the body. The pose position is the body centre, so the offsets are small.
public static class HeldLightAnchor
{
    public const float FirstPersonRight = 0.25f;
    public const float FirstPersonDown = 0.15f;
    public const float FirstPersonForward = 0.4f;

    // Between the idle off-hand torch head lowered (0.01 up, 0.26 ahead) and raised (0.25 up, 0.46 ahead), 0.12 to the
    // left; HeldFlamePointPoseTests.BodyAnchor_IsCloseToTheIdleOffHandFlame pins it to the real pose.
    public const float BodyUp = 0.15f;
    public const float BodyForward = 0.35f;
    public const float BodyRight = -0.12f;

    // camera + right·0.25 − up·0.15 + front·0.4.
    public static Vector3 FirstPerson(Vector3 cameraPosition, Vector3 front, Vector3 right, Vector3 up)
    {
        return cameraPosition + right * FirstPersonRight - up * FirstPersonDown + front * FirstPersonForward;
    }

    // pose + up·0.15 + facing·0.35 + right·(-0.12), with facing flattened to the horizontal plane and
    // right = facing × up. A facing with no horizontal part (zero or straight up or down) gives no
    // forward or sideways offset.
    public static Vector3 Body(Vector3 posePosition, Vector3 facing)
    {
        Vector3 anchor = posePosition + Vector3.UnitY * BodyUp;
        Vector3 horizontal = new(facing.X, 0f, facing.Z);
        float lengthSquared = horizontal.LengthSquared;
        if (!(lengthSquared > 1e-8f) || !float.IsFinite(lengthSquared))
        {
            return anchor;
        }

        horizontal /= MathF.Sqrt(lengthSquared);
        Vector3 right = new(-horizontal.Z, 0f, horizontal.X); // Vector3.Cross(horizontal, UnitY)
        return anchor + horizontal * BodyForward + right * BodyRight;
    }
}
