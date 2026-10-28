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
    // Each slot animates its own instance over the shared viewmodel model, so the hands and the
    // held item never advance or restart each other's clip, even when they use the same model.
    private readonly ViewModelSlotAnimation _handsAnimation = new();
    private readonly ViewModelSlotAnimation _heldItemAnimation = new();

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
            UpdateSlot(frameState.Value.Hands, _handsAnimation, deltaTime),
            UpdateSlot(frameState.Value.HeldItem, _heldItemAnimation, deltaTime));
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

        DrawSlot(frameState.Value.Hands, _handsAnimation);
        DrawSlot(frameState.Value.HeldItem, _heldItemAnimation);
    }

    // A model that is still loading reports "not finished", so a Use request simply waits: the
    // controller keeps the slot in Use, the one-shot plays once the model is ready, and the slot
    // then finishes and returns to Idle as usual. A model that failed to load also reports
    // "not finished" forever, leaving the slot in Use: intentional and harmless, since nothing is
    // drawn for it (and changing the held item resets that slot to Idle).
    // The slot's clip advances here only, once per frame; Draw only starts clips and draws.
    private static bool UpdateSlot(ViewModelSlotState slotState, ViewModelSlotAnimation animation, float deltaTime)
    {
        return TryResolveModel(slotState, out AnimatedModel? model) && animation.Bind(model).Update(deltaTime);
    }

    private void DrawSlot(ViewModelSlotState slotState, ViewModelSlotAnimation animation)
    {
        if (!TryResolveModel(slotState, out AnimatedModel? model))
        {
            return;
        }

        AnimatedModelInstance instance = animation.Bind(model);

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

        ApplyAnimation(instance, slotState);

        Shader shader = _render.GetShader("cube");
        model.BeginRender(shader, _viewModelView, projection);
        model.RenderPrepared(shader, instance, modelMatrix);
        model.EndRender();
    }

    private static void ApplyAnimation(AnimatedModelInstance instance, ViewModelSlotState slotState)
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
            instance.PlayOneShot(clipName);
            return;
        }

        instance.PlayLoop(clipName);
    }

    private static bool TryResolveModel(ViewModelSlotState slotState, [NotNullWhen(true)] out AnimatedModel? model)
    {
        return ModelAssets.TryGet(slotState.Definition.ModelPath, out model);
    }
}

// One viewmodel slot's animation instance. It is rebound only when the slot's model changes (a
// different held item, or a model reloaded after ModelAssets.DisposeAll), so a steady frame
// allocates nothing. Instances own no GL resources.
internal sealed class ViewModelSlotAnimation
{
    private AnimatedModelInstance? _instance;

    public AnimatedModelInstance Bind(AnimatedModel model)
    {
        if (_instance is null || !ReferenceEquals(_instance.Skeleton, model.Skeleton))
        {
            _instance = model.CreateInstance();
        }

        return _instance;
    }
}
