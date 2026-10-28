using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.Players;

namespace Dream.Rendering.Actors;

public sealed class LocalPlayerRenderer : IDisposable
{
    private const int LocalActorId = 0;

    private readonly Render _render;
    private readonly PlayerAnimationClipTracker _clipTracker = new();
    private AnimatedModelInstanceHandle? _model;
    private bool _disposed;

    public LocalPlayerRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(LocalPlayerPresentationState localPlayer, Camera camera, float deltaTime)
    {
        if (_disposed)
        {
            return;
        }

        // Draw runs only when third person is shown, so the player's instance is requested on
        // the first such draw and kept afterwards. The shared player model loads in the
        // background; skip the draw until it is uploaded. The clip is applied (and recorded) only
        // once the instance exists.
        _model ??= ModelAssets.RequestPlayerInstance();
        if (!_model.TryGet(out AnimatedModel? model, out AnimatedModelInstance? instance))
        {
            return;
        }

        PlayerRenderState renderState = PlayerRenderState.CreateLocal(
            localPlayer.Position,
            localPlayer.Facing,
            localPlayer.IsMoving);
        _clipTracker.TryApplyClip(LocalActorId, renderState.ClipName, instance);
        instance.Update(deltaTime);

        Shader shader = _render.GetShader(renderState.ShaderName);
        model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
        model.RenderPrepared(shader, instance, renderState.ModelMatrix, renderState.NodeOverrides);
        model.EndRender();
    }

    // The player's instance handle is a per-instance request, so this renderer owns it: disposing
    // it drops the instance. Draw never requests after Dispose. The shared ModelAssets player
    // model is never disposed here.
    public void Dispose()
    {
        _disposed = true;
        AnimatedModelInstanceHandle? model = _model;
        _model = null;
        model?.Dispose();
    }
}
