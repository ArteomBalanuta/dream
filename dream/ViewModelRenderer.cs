using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

public readonly record struct ViewModelUpdateResult(
    bool HandsFinished,
    bool HeldItemFinished);

public class ViewModelRenderer
{
    private readonly Render _render;
    private readonly Matrix4 _viewModelView = Matrix4.Identity;

    public ViewModelRenderer(Render render)
    {
        _render = render;
    }

    public ViewModelUpdateResult Update(ViewModelFrameState? frameState, float deltaTime)
    {
        if (frameState is null)
        {
            return default;
        }

        return new ViewModelUpdateResult(
            UpdateSlot(frameState.Value.Hands, deltaTime),
            UpdateSlot(frameState.Value.HeldItem, deltaTime));
    }

    public void Draw(ViewModelFrameState? frameState)
    {
        if (frameState is null)
        {
            return;
        }

        // Viewmodels intentionally render on top of the world pass.
        // Clearing only depth preserves color while preventing world geometry from clipping the first-person pass.
        GL.Clear(ClearBufferMask.DepthBufferBit);

        DrawSlot(frameState.Value.Hands, 60f);
        DrawSlot(frameState.Value.HeldItem, 60f);
    }

    private bool UpdateSlot(ViewModelSlotState slotState, float deltaTime)
    {
        return ResolveModel(slotState).Update(deltaTime);
    }

    private void DrawSlot(ViewModelSlotState slotState, float fovDegrees)
    {
        AnimatedModel model = ResolveModel(slotState);
        Vector2i viewportSize = _render.ViewportSize;
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(fovDegrees),
            (float)viewportSize.X / viewportSize.Y,
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

    private static AnimatedModel ResolveModel(ViewModelSlotState slotState)
    {
        return ModelAssets.Get(slotState.Definition.ModelPath);
    }
}
