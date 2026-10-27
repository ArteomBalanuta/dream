using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Rendering.Actors;


public sealed class EnemyRenderer
{
    private readonly Render _render;
    private readonly EnemyVisualStateCache _visualStates = new();
    private readonly HashSet<int> _currentIds = new();

    public EnemyRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(Camera camera, IReadOnlyList<EnemyActor> actors, float deltaTime, bool showCollisionGeometry)
    {
        _currentIds.Clear();
        foreach (EnemyActor actor in actors)
        {
            if (!actor.IsActive)
            {
                continue;
            }

            int actorId = actor.Combatant.Actor.Id;
            _currentIds.Add(actorId);

            EnemyRenderState renderState = EnemyRenderState.CreateWolf(
                actor.Position,
                actor.Forward,
                actor.BehaviorState);

            // The shared wolf model loads in the background; until it is ready the enemy is not
            // drawn and no clip is recorded, so the clip is applied on the first drawn frame.
            if (ModelAssets.TryGet(renderState.ModelPath, out AnimatedModel? model))
            {
                _visualStates.GetOrAdd(actorId).ApplyClip(model, renderState.ClipName);
                model.Update(deltaTime);

                Shader shader = _render.GetShader(renderState.ShaderName);
                model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
                model.RenderPrepared(shader, renderState.ModelMatrix);
                model.EndRender();
            }

            if (showCollisionGeometry)
            {
                Matrix4 debugMatrix =
                    Matrix4.CreateScale(actor.CollisionRadius * 2f, actor.CollisionHeight, actor.CollisionRadius * 2f) *
                    Matrix4.CreateTranslation(actor.Position + new Vector3(0f, actor.CollisionHeight * 0.5f, 0f));
                _render.DrawCollisionShape(debugMatrix, camera);
            }
        }

        _visualStates.PruneExcept(_currentIds);
    }
}
