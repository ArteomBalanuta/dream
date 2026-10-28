using OpenTK.Mathematics;
using Dream.Client.Presentation;
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

    // Draws every enemy at its authoritative pose.
    public void Draw(Camera camera, IReadOnlyList<EnemyActor> actors, float deltaTime, bool showCollisionGeometry)
    {
        DrawAtRenderTick(camera, actors, deltaTime, showCollisionGeometry, poseSource: null, renderTick: 0d);
    }

    // With a pose source, enemies draw at their pose interpolated for renderTick on the server-tick
    // timeline. EnemyActor.Position is never written here: gameplay, hit tests and the collision
    // debug shape keep the latest authoritative position.
    public void DrawAtRenderTick(
        Camera camera,
        IReadOnlyList<EnemyActor> actors,
        float deltaTime,
        bool showCollisionGeometry,
        IEnemyRenderPoseSource? poseSource,
        double renderTick)
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

            InterpolatedActorPose pose = ResolveRenderPose(actor, poseSource, renderTick);
            EnemyRenderState renderState = EnemyRenderState.CreateWolf(
                pose.Position,
                pose.Facing,
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

    // The interpolated pose when the source has one for this enemy, else the authoritative pose.
    // A degenerate interpolated facing falls back to the actor's authoritative forward.
    public static InterpolatedActorPose ResolveRenderPose(
        EnemyActor actor,
        IEnemyRenderPoseSource? poseSource,
        double renderTick)
    {
        if (poseSource is not null &&
            poseSource.TryGetEnemyRenderPose(actor.Combatant.Actor.Id, renderTick, out InterpolatedActorPose pose))
        {
            return pose.Facing.LengthSquared > 0.0001f ? pose : pose with { Facing = actor.Forward };
        }

        return new InterpolatedActorPose(actor.Position, actor.Forward);
    }
}
