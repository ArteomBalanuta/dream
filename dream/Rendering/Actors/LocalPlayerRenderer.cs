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
    private AnimatedModel? _model;

    public LocalPlayerRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(LocalPlayerPresentationState localPlayer, Camera camera, float deltaTime)
    {
        PlayerRenderState renderState = PlayerRenderState.CreateLocal(
            localPlayer.Position,
            localPlayer.Facing,
            localPlayer.IsMoving);
        AnimatedModel model = GetOrCreateModel();
        _clipTracker.TryApplyClip(LocalActorId, renderState.ClipName, model);
        model.Update(deltaTime);

        Shader shader = _render.GetShader(renderState.ShaderName);
        model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
        model.RenderPrepared(shader, renderState.ModelMatrix, renderState.NodeOverrides);
        model.EndRender();
    }

    // The player model is a per-instance copy (ModelAssets.CreatePlayerInstance), so this renderer
    // owns it. Shared ModelAssets models are never disposed here.
    public void Dispose()
    {
        AnimatedModel? model = _model;
        _model = null;
        model?.Dispose();
    }

    private AnimatedModel GetOrCreateModel()
    {
        _model ??= ModelAssets.CreatePlayerInstance();
        return _model;
    }
}
