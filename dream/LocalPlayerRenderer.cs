public sealed class LocalPlayerRenderer
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
        _clipTracker.TryApplyClip(LocalActorId, renderState.ClipName, model.PlayLoop);
        model.Update(deltaTime);

        Shader shader = _render.GetShader(renderState.ShaderName);
        model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
        model.RenderPrepared(shader, renderState.ModelMatrix, renderState.NodeOverrides);
        model.EndRender();
    }

    private AnimatedModel GetOrCreateModel()
    {
        _model ??= ModelAssets.CreatePlayerInstance();
        return _model;
    }
}
