using OpenTK.Mathematics;

public class ViewModelRenderer
{
    private readonly Render _render;
    private readonly Matrix4 _viewModelView = Matrix4.Identity;

    public ViewModelRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(Camera camera, ViewModelFrameState? frameState)
    {
        if (frameState is null)
        {
            return;
        }

        DrawSlot(ModelAssets.Get(frameState.Value.Hands.Definition.ModelPath), frameState.Value.Hands, 60f);
        DrawSlot(ModelAssets.Get(frameState.Value.HeldItem.Definition.ModelPath), frameState.Value.HeldItem, 60f);
    }

    private void DrawSlot(AnimatedModel model, ViewModelSlotState slotState, float fovDegrees)
    {
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(fovDegrees),
            16f / 9f,
            0.01f,
            10f);

        Matrix4 modelMatrix =
            Matrix4.CreateScale(slotState.Definition.Scale) *
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(slotState.Definition.RotationOffsetDegrees.X)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(slotState.Definition.RotationOffsetDegrees.Y)) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(slotState.Definition.RotationOffsetDegrees.Z)) *
            Matrix4.CreateTranslation(slotState.Definition.PositionOffset);

        ApplyAnimation(model, slotState);

        Shader shader = _render.GetShader("cube");
        model.BeginRender(shader, _viewModelView, projection);
        model.RenderPrepared(shader, modelMatrix);
        model.EndRender();
    }

    private static void ApplyAnimation(AnimatedModel model, ViewModelSlotState slotState)
    {
        string? clipName = slotState.AnimationRequest == ViewModelAnimationRequest.Use
            ? slotState.Definition.UseAnimation
            : slotState.Definition.IdleAnimation;

        if (string.IsNullOrWhiteSpace(clipName))
        {
            return;
        }

        if (slotState.AnimationRequest == ViewModelAnimationRequest.Use)
        {
            model.PlayOneShot(clipName);
            return;
        }

        model.PlayLoop(clipName);
    }
}
