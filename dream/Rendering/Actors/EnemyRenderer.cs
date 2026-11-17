using OpenTK.Mathematics;
using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.Shadows;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Rendering.Actors;


public sealed class EnemyRenderer
{
    private readonly Render _render;
    private readonly EnemyVisualStateCache _visualStates = new();
    private readonly HashSet<int> _currentIds = new();
    private readonly ShadowActorCasters _casters = new();

    public EnemyRenderer(Render render)
    {
        _render = render;
    }

    // The enemies the last Draw put on screen, drawn depth-only into a shadow map (see ShadowActorCasters).
    public int DrawCasters(in ShadowCasterView view) => _casters.Draw(_render, in view);

    public void ClearCasters() => _casters.Clear();

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
        _casters.Clear();
        foreach (EnemyActor actor in actors)
        {
            if (!actor.IsActive)
            {
                continue;
            }

            int actorId = actor.Combatant.Actor.Id;
            _currentIds.Add(actorId);

            InterpolatedActorPose pose = ResolveRenderPose(actor, poseSource, renderTick);
            EnemyVisual visual = EnemyVisualCatalog.Resolve(actor.VisualKey);

            // The shared model loads in the background; until it is ready the enemy is not
            // drawn and no clip is recorded, so the clip is applied on the first drawn frame.
            // The model (GPU buffers, skeleton, clips) is shared; each enemy animates its own
            // instance, which advances exactly once per frame by this frame's dt.
            if (ModelAssets.TryGet(visual.ModelPath, out AnimatedModel? model))
            {
                // The gait is measured here, from the pose actually drawn: the client holds an
                // enemy's speed nowhere (only BehaviorState replicates), so a walk and a gallop can
                // only be told apart by how far the interpolated pose moved this frame.
                EnemyVisualState visualState = _visualStates.GetOrAdd(actorId, model.Skeleton);
                visualState.ObserveMotion(pose.Position, deltaTime);
                EnemyClipSet clips = visual.Clips;
                EnemyRenderState renderState = EnemyRenderState.Create(
                    in visual,
                    pose.Position,
                    pose.Facing,
                    actor.BehaviorState,
                    scale: null,
                    speed: visualState.ResolveGaitSpeed(in clips, actor.BehaviorState));

                AnimatedModelInstance instance = AdvanceInstance(
                    visualState,
                    renderState.ClipName,
                    deltaTime,
                    renderState.PlaybackSpeed);

                Shader shader = _render.GetShader(renderState.ShaderName);
                model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
                ApplyTint(model, shader, visual.TintOffset);
                model.RenderPrepared(shader, instance, renderState.ModelMatrix);
                model.EndRender();
                _casters.Add(model, instance, renderState.ModelMatrix);
            }

            if (showCollisionGeometry)
            {
                Matrix4 debugMatrix =
                    Matrix4.CreateScale(actor.CollisionRadius * 2f, actor.CollisionHeight, actor.CollisionRadius * 2f) *
                    Matrix4.CreateTranslation(actor.Position + new Vector3(0f, actor.CollisionHeight * 0.5f, 0f));
                _render.DrawCollisionShape(debugMatrix, camera);
            }
        }

        // The player renderers share the enemy program, so the tint must not outlive the enemies.
        ResetTint();

        _visualStates.PruneExcept(_currentIds);
    }

    // The tint last uploaded, the program it went to and the model that carries it to the MASK
    // variant on a program switch (AnimatedModel.ActorTintOffset).
    private Vector3 _appliedTint;
    private Shader? _tintShader;
    private AnimatedModel? _tintModel;

    // Set the moment a non-zero tint is handed to a model: AnimatedModel.BindProgram may have carried
    // it into the MASK variant, and a later untinted enemy drives _appliedTint back to zero without
    // touching the variant, so the base program's value alone cannot say whether the variant is clean.
    private bool _maskTintDirty;

    private void ApplyTint(AnimatedModel model, Shader shader, Vector3 tint)
    {
        model.ActorTintOffset = tint;
        _maskTintDirty |= !tint.Equals(Vector3.Zero);
        _tintShader = shader;
        _tintModel = model;
        if (tint.Equals(_appliedTint))
        {
            return;
        }

        // The base program is bound by BeginRender; its MASK variant is written by the model on a switch.
        shader.SetVector3(TintUniformName, tint);
        _appliedTint = tint;
    }

    // Back to zero in the base program, the model and the MASK variant. A no-op when nothing was tinted.
    private void ResetTint()
    {
        if (_tintShader is null || _tintModel is null || (_appliedTint.Equals(Vector3.Zero) && !_maskTintDirty))
        {
            return;
        }

        _tintModel.ActorTintOffset = Vector3.Zero;
        _tintShader.Use();
        _tintShader.SetVector3(TintUniformName, Vector3.Zero);
        if (_tintShader.AlphaMaskVariant is { } maskProgram)
        {
            maskProgram.Use();
            maskProgram.SetVector3(TintUniformName, Vector3.Zero);
            _tintShader.Use();
        }

        _appliedTint = Vector3.Zero;
        _maskTintDirty = false;
    }

    private const string TintUniformName = "uActorTintOffset";

    // One enemy's per-frame animation step: start the clip if it changed, set the rate it plays at,
    // then advance the enemy's own instance by deltaTime. Called once per enemy per frame; allocates
    // nothing. The default rate of 1 is the as-authored playback every caller had before gaits.
    public static AnimatedModelInstance AdvanceInstance(
        EnemyVisualState state,
        string clipName,
        float deltaTime,
        float playbackSpeed = 1f)
    {
        ArgumentNullException.ThrowIfNull(state);
        AnimatedModelInstance instance = state.Instance
            ?? throw new InvalidOperationException("The enemy state has no animation instance; bind it first.");
        state.ApplyClip(clipName);
        instance.AnimationSpeed = playbackSpeed;
        instance.Update(deltaTime);
        return instance;
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
