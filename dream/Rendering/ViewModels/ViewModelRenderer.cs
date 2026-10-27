using System.Diagnostics.CodeAnalysis;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Rendering.Models;

namespace Dream.Rendering.ViewModels;


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

        DrawSlot(frameState.Value.Hands);
        DrawSlot(frameState.Value.HeldItem);
    }

    // A model that is still loading reports "not finished", so a Use request simply waits: the
    // controller keeps the slot in Use, the one-shot plays once the model is ready, and the slot
    // then finishes and returns to Idle as usual.
    private static bool UpdateSlot(ViewModelSlotState slotState, float deltaTime)
    {
        return TryResolveModel(slotState, out AnimatedModel? model) && model.Update(deltaTime);
    }

    private void DrawSlot(ViewModelSlotState slotState)
    {
        if (!TryResolveModel(slotState, out AnimatedModel? model))
        {
            return;
        }

        Vector2i viewportSize = _render.ViewportSize;
        ViewModelResolvedPose pose = slotState.ResolvedPose;
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(pose.FieldOfViewDegrees),
            (float)viewportSize.X / viewportSize.Y,
            0.01f,
            10f);

        Matrix4 modelMatrix =
            Matrix4.CreateScale(pose.Scale) *
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(pose.RotationOffsetDegrees.X)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(pose.RotationOffsetDegrees.Y)) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(pose.RotationOffsetDegrees.Z)) *
            Matrix4.CreateTranslation(pose.PositionOffset);

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

    private static bool TryResolveModel(ViewModelSlotState slotState, [NotNullWhen(true)] out AnimatedModel? model)
    {
        return ModelAssets.TryGet(slotState.Definition.ModelPath, out model);
    }
}
