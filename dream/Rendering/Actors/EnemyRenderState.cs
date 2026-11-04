using OpenTK.Mathematics;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Rendering.Actors;


public readonly record struct EnemyRenderState(
    string ModelPath,
    string ShaderName,
    string ClipName,
    Matrix4 ModelMatrix)
{
    // One enemy's draw, from its visual (model, clips, scale) and its pose. The visual is what
    // EnemyVisualCatalog resolved for the key the server sent, so a second enemy model is a
    // catalog entry and not a branch here.
    public static EnemyRenderState Create(
        in EnemyVisual visual,
        Vector3 position,
        Vector3 forward,
        EnemyBehaviorState behaviorState,
        float? scale = null)
    {
        Vector3 facing = forward.LengthSquared > 0.0001f ? forward.Normalized() : -Vector3.UnitZ;
        float yawRadians = MathF.Atan2(facing.X, facing.Z);
        float resolvedScale = scale ?? visual.ResolveScale();
        Matrix4 modelMatrix =
            Matrix4.CreateScale(resolvedScale) *
            Matrix4.CreateRotationY(yawRadians) *
            Matrix4.CreateTranslation(position);

        return new EnemyRenderState(
            visual.ModelPath,
            "enemy",
            visual.Clips.Resolve(behaviorState),
            modelMatrix);
    }

    // The default (wolf) visual, for callers that have no key.
    public static EnemyRenderState CreateWolf(
        Vector3 position,
        Vector3 forward,
        EnemyBehaviorState behaviorState,
        float? scale = null) =>
        Create(EnemyVisualCatalog.Default, position, forward, behaviorState, scale);
}
