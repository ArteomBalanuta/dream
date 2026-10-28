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
    private AsyncResourceHandle<AnimatedModel>? _model;
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

        // Draw runs only when third person is shown, so the per-instance model is requested on
        // the first such draw and kept afterwards. It loads in the background; skip the draw
        // until it is uploaded. The clip is applied (and recorded) only once the model exists.
        _model ??= ModelAssets.RequestPlayerInstance();
        if (!_model.TryGet(out AnimatedModel? model))
        {
            return;
        }

        PlayerRenderState renderState = PlayerRenderState.CreateLocal(
            localPlayer.Position,
            localPlayer.Facing,
            localPlayer.IsMoving);
        _clipTracker.TryApplyClip(LocalActorId, renderState.ClipName, model);
        model.Update(deltaTime);

        Shader shader = _render.GetShader(renderState.ShaderName);
        model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
        model.RenderPrepared(shader, renderState.ModelMatrix, renderState.NodeOverrides);
        model.EndRender();
    }

    // The player model handle is a per-instance request, so this renderer owns it: disposing it
    // releases the uploaded model or discards a pending decode. Draw never requests after Dispose.
    // Shared ModelAssets models are never disposed here.
    public void Dispose()
    {
        _disposed = true;
        AsyncResourceHandle<AnimatedModel>? model = _model;
        _model = null;
        model?.Dispose();
    }
}
