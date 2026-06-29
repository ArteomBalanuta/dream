using OpenTK.Mathematics;

public sealed class EnemyRenderer
{
    private readonly Render _render;

    public EnemyRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(Camera camera, IReadOnlyList<EnemyActor> actors, float deltaTime, bool showCollisionGeometry)
    {
        foreach (EnemyActor actor in actors)
        {
            if (!actor.IsActive)
            {
                continue;
            }

            EnemyRenderState renderState = EnemyRenderState.CreateWolf(
                actor.Position,
                actor.Forward,
                actor.BehaviorState);

            AnimatedModel model = ModelAssets.Get(renderState.ModelPath);
            actor.VisualState.ApplyClip(model, renderState.ClipName);
            model.Update(deltaTime);

            Shader shader = _render.GetShader(renderState.ShaderName);
            model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
            model.RenderPrepared(shader, renderState.ModelMatrix);
            model.EndRender();

            if (showCollisionGeometry)
            {
                Matrix4 debugMatrix =
                    Matrix4.CreateScale(actor.CollisionRadius * 2f, actor.CollisionHeight, actor.CollisionRadius * 2f) *
                    Matrix4.CreateTranslation(actor.Position + new Vector3(0f, actor.CollisionHeight * 0.5f, 0f));
                _render.DrawCollisionShape(debugMatrix, camera);
            }
        }
    }
}
