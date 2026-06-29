using OpenTK.Mathematics;

public readonly record struct EnemyRenderState(
    string ModelPath,
    string ShaderName,
    string ClipName,
    Matrix4 ModelMatrix)
{
    public static EnemyRenderState CreateWolf(
        Vector3 position,
        Vector3 forward,
        EnemyBehaviorState behaviorState,
        float? scale = null)
    {
        Vector3 facing = forward.LengthSquared > 0.0001f ? forward.Normalized() : -Vector3.UnitZ;
        float yawRadians = MathF.Atan2(facing.X, facing.Z);
        float resolvedScale = scale ?? WolfAssetDefinition.VisualScale;
        Matrix4 modelMatrix =
            Matrix4.CreateScale(resolvedScale) *
            Matrix4.CreateRotationY(yawRadians) *
            Matrix4.CreateTranslation(position);

        return new EnemyRenderState(
            WolfAssetDefinition.ModelPath,
            "enemy",
            WolfAnimationResolver.ResolveClip(behaviorState),
            modelMatrix);
    }
}
