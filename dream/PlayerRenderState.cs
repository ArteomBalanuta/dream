using OpenTK.Mathematics;

public readonly record struct PlayerRenderState(
    string ModelPath,
    string ShaderName,
    string ClipName,
    Matrix4 ModelMatrix,
    IReadOnlyDictionary<string, Matrix4> NodeOverrides)
{
    public static PlayerRenderState CreateLocal(Vector3 position, Vector3 facing, bool isMoving, float? scale = null)
    {
        return Create(
            position,
            facing,
            isMoving,
            scale);
    }

    public static PlayerRenderState CreateRemote(RemotePlayerPresentationState player, float? scale = null)
    {
        return Create(
            player.Pose.Position,
            player.Pose.Facing,
            player.IsMoving,
            scale);
    }

    private static PlayerRenderState Create(Vector3 position, Vector3 facing, bool isMoving, float? scale)
    {
        Vector3 resolvedFacing = facing.LengthSquared > 0.0001f
            ? facing.Normalized()
            : PlayerModelAssetDefinition.AuthoredForwardAxis;
        float yawRadians = ResolveYawRadians(PlayerModelAssetDefinition.AuthoredForwardAxis, resolvedFacing);
        float resolvedScale = scale ?? PlayerModelAssetDefinition.VisualScale;
        Matrix4 modelMatrix =
            Matrix4.CreateScale(resolvedScale) *
            Matrix4.CreateRotationY(yawRadians) *
            Matrix4.CreateTranslation(position + PlayerModelAssetDefinition.BodyCenterToFootOffset);

        return new PlayerRenderState(
            PlayerModelAssetDefinition.ModelPath,
            "enemy",
            isMoving ? PlayerModelAssetDefinition.WalkClip : PlayerModelAssetDefinition.IdleClip,
            modelMatrix,
            CreateNodeOverrides());
    }

    private static IReadOnlyDictionary<string, Matrix4> CreateNodeOverrides()
    {
        return new Dictionary<string, Matrix4>
        {
            [PlayerModelAssetDefinition.HeadNodeName] = Matrix4.Identity
        };
    }

    private static float ResolveYawRadians(Vector3 authoredForward, Vector3 desiredForward)
    {
        Vector3 authored = Flatten(authoredForward);
        Vector3 desired = Flatten(desiredForward);
        if (authored == Vector3.Zero || desired == Vector3.Zero)
        {
            return 0f;
        }

        float authoredYaw = MathF.Atan2(authored.X, authored.Z);
        float desiredYaw = MathF.Atan2(desired.X, desired.Z);
        return desiredYaw - authoredYaw;
    }

    private static Vector3 Flatten(Vector3 vector)
    {
        Vector3 flattened = new(vector.X, 0f, vector.Z);
        return flattened.LengthSquared <= 0.0001f ? Vector3.Zero : flattened.Normalized();
    }
}
