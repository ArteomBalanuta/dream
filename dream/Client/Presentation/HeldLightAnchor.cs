using OpenTK.Mathematics;

namespace Dream.Client.Presentation;

// Where a held light sits in the world (wave T2): near the right hand. First person anchors to the
// camera so the light follows the view; third person and remote players anchor to the body pose
// (the actor's world position) and its horizontal facing.
public static class HeldLightAnchor
{
    public const float FirstPersonRight = 0.25f;
    public const float FirstPersonDown = 0.15f;
    public const float FirstPersonForward = 0.4f;

    public const float BodyUp = 1.3f;
    public const float BodyForward = 0.3f;
    public const float BodyRight = 0.3f;

    // camera + right·0.25 − up·0.15 + front·0.4.
    public static Vector3 FirstPerson(Vector3 cameraPosition, Vector3 front, Vector3 right, Vector3 up)
    {
        return cameraPosition + right * FirstPersonRight - up * FirstPersonDown + front * FirstPersonForward;
    }

    // pose + up·1.3 + facing·0.3 + right·0.3, with facing flattened to the horizontal plane and
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
