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

    // Render-only: the models of enemies that have already left the registry, still playing out
    // their death clip. See EnemyDeathLinger - a dying model is not an entity.
    private readonly EnemyDeathLinger _dying = new();
    private readonly List<int> _departedIds = new();

    public EnemyRenderer(Render render)
    {
        _render = render;
    }

    // The enemies the last Draw put on screen, drawn depth-only into a shadow map (see ShadowActorCasters).
    public int DrawCasters(in ShadowCasterView view) => _casters.Draw(_render, in view);

    public void ClearCasters() => _casters.Clear();

    // Drops every corpse and every remembered last-drawn frame. Called when the world under the
    // renderer is replaced (a join applied mid-session: a reconnect, a takeover, a world transfer).
    // The old world's enemies do not die, they cease to exist, but the linger cannot tell the two
    // apart: without this the next draw finds every old actor id in its last-drawn table and absent
    // from the new world's id set, and starts a death clip for each one - frozen at its OLD-world
    // matrix, drawn and casting shadows in the new world for up to MaxLingerSeconds.
    //
    // Render-only, exactly as the linger is: the instances go back to the visual-state pool and
    // nothing outside this renderer is touched. The linger's other guarantees are unchanged - a
    // corpse still holds no actor, no authoritative pose and no interpolation buffer, it is still
    // bounded by its clip and by MaxLingerSeconds, and ObserveDrawn still releases one under a
    // reused id so a respawn draws no corpse under itself.
    public void ClearDeathLinger() => _dying.Clear(_visualStates);

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
        // Releases the corpses that finished last frame and steps the rest, before any of this
        // frame's live enemies are looked at.
        _dying.Advance(deltaTime, _visualStates);
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
                ApplyTint(
                    model,
                    shader,
                    ActorHighlight.EnemyTint(HighlightActorId, actorId, visual.TintOffset),
                    ActorHighlight.RimFor(HighlightActorId, actorId),
                    camera.Position);
                model.RenderPrepared(shader, instance, renderState.ModelMatrix);
                model.EndRender();
                _casters.Add(model, instance, renderState.ModelMatrix);

                // The frame this enemy was last drawn at, so its model can finish a death clip
                // after the actor is gone. A matrix and a clip name - no pose source, no actor.
                // Also releases any corpse under this id: enemy actor ids are reused, and a
                // respawn must not draw a corpse under itself.
                _dying.ObserveDrawn(
                    actorId,
                    new EnemyDeathLinger.LastDrawnEnemy(
                        visual.ModelPath,
                        renderState.ShaderName,
                        clips.Die,
                        renderState.ModelMatrix,
                        visual.TintOffset,
                        model.Skeleton),
                    _visualStates);
            }

            if (showCollisionGeometry)
            {
                Matrix4 debugMatrix =
                    Matrix4.CreateScale(actor.CollisionRadius * 2f, actor.CollisionHeight, actor.CollisionRadius * 2f) *
                    Matrix4.CreateTranslation(actor.Position + new Vector3(0f, actor.CollisionHeight * 0.5f, 0f));
                _render.DrawCollisionShape(debugMatrix, camera);
            }
        }

        BeginDeathLinger();
        DrawDyingModels(camera);

        // The player renderers share the enemy program, so the tint must not outlive the enemies -
        // the dying models above are tinted like the enemies they were, so this still follows the
        // last enemy draw of the frame.
        ResetTint();

        _visualStates.PruneExcept(_currentIds);
    }

    // An enemy drawn last frame and absent from this one has died: the client drops a dead enemy
    // from its registry (RemoteEnemyStateApplier) and the presentation filters a locally killed one
    // out (WorldSessionPresentationState.SelectActiveEnemies), so a departure is the only death
    // signal the renderer ever gets. Nothing is started for a model that is no longer loaded or
    // that carries no death clip, so the state machine's promise of Resolve(Dead) is kept exactly
    // where the clip exists.
    private void BeginDeathLinger()
    {
        if (_dying.LastDrawnCount == 0)
        {
            return;
        }

        _dying.CollectDeparted(_currentIds, _departedIds);
        for (int i = 0; i < _departedIds.Count; i++)
        {
            int actorId = _departedIds[i];
            if (!_dying.TryGetLastDrawn(actorId, out EnemyDeathLinger.LastDrawnEnemy last))
            {
                continue;
            }

            // The departed enemy's own instance, taken out of its visual state before PruneExcept
            // can pool it: the corpse animates the very instance that was drawing the enemy, so a
            // death allocates nothing, and the instance returns to the pool when the clip ends.
            AnimatedModelInstance? instance = _visualStates.TakeInstance(actorId);
            if (instance is null ||
                !ModelAssets.TryGet(last.ModelPath, out AnimatedModel? model) ||
                !ReferenceEquals(model.Skeleton, last.Skeleton) ||
                !ReferenceEquals(instance.Skeleton, last.Skeleton) ||
                !model.Skeleton.TryGetClip(last.DieClip, out _, out float duration) ||
                !(duration > 0f))
            {
                _dying.Forget(actorId);
                if (instance is not null)
                {
                    _visualStates.ReturnInstance(instance);
                }

                continue;
            }

            _dying.Begin(actorId, in last, instance);
        }

        _departedIds.Clear();
    }

    // The corpses, at the matrix each was frozen at. A model reloaded under a corpse (ModelAssets
    // replaced the skeleton) is skipped and expires on its own cap rather than drawing against
    // buffers that have been thrown away.
    private void DrawDyingModels(Camera camera)
    {
        if (_dying.Count == 0)
        {
            return;
        }

        foreach (EnemyDeathLinger.DyingModel dying in _dying.Models)
        {
            if (!ModelAssets.TryGet(dying.Last.ModelPath, out AnimatedModel? model) ||
                !ReferenceEquals(model.Skeleton, dying.Instance.Skeleton))
            {
                continue;
            }

            Shader shader = _render.GetShader(dying.Last.ShaderName);
            model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
            // A corpse is never rimmed, whatever id the hover holds.
            ApplyTint(model, shader, dying.Last.TintOffset, Vector4.Zero, camera.Position);
            model.RenderPrepared(shader, dying.Instance, dying.Last.ModelMatrix);
            model.EndRender();
            _casters.Add(model, dying.Instance, dying.Last.ModelMatrix);
        }
    }

    // The enemy the world hover rests on (combat actor id): drawn lifted. A corpse in the death linger is
    // never highlighted, it keeps its plain tint.
    public int? HighlightActorId { get; set; }

    // The tint last uploaded, the program it went to and the model that carries it to the MASK
    // variant on a program switch (AnimatedModel.ActorTintOffset).
    private Vector3 _appliedTint;
    private Shader? _tintShader;
    private AnimatedModel? _tintModel;

    // Set the moment a non-zero tint is handed to a model: AnimatedModel.BindProgram may have carried
    // it into the MASK variant, and a later untinted enemy drives _appliedTint back to zero without
    // touching the variant, so the base program's value alone cannot say whether the variant is clean.
    private bool _maskTintDirty;

    // The hover rim (uActorRim / uActorRimEye) last uploaded to the base program, same bookkeeping as the tint.
    // The rim shares _maskTintDirty: it is set when a non-zero rim is handed to a model.
    private Vector4 _appliedRim;

    private void ApplyTint(AnimatedModel model, Shader shader, Vector3 tint, Vector4 rim, Vector3 rimEye)
    {
        model.ActorTintOffset = tint;
        model.ActorRim = rim;
        model.ActorRimEye = rimEye;
        _maskTintDirty |= !tint.Equals(Vector3.Zero) || !rim.Equals(Vector4.Zero);
        _tintShader = shader;
        _tintModel = model;
        if (!tint.Equals(_appliedTint))
        {
            // The base program is bound by BeginRender; its MASK variant is written by the model on a switch.
            shader.SetVector3(TintUniformName, tint);
            _appliedTint = tint;
        }

        if (!rim.Equals(_appliedRim))
        {
            shader.SetVector4(RimUniformName, rim);
            _appliedRim = rim;
        }

        // The eye is uploaded on EVERY rimmed draw, never from a cache: other draws of this program (the ground
        // scatter's foliage, through AnimatedModel.BindProgram) write their own zero eye into it between frames,
        // so a cached "already there" would leave a still camera's hovered enemy rimmed toward the world origin.
        // One upload per hovered draw.
        if (!rim.Equals(Vector4.Zero))
        {
            shader.SetVector3(RimEyeUniformName, rimEye);
        }
    }

    // Back to zero in the base program, the model and the MASK variant. A no-op when nothing was tinted.
    private void ResetTint()
    {
        if (_tintShader is null || _tintModel is null || (_appliedTint.Equals(Vector3.Zero) && _appliedRim.Equals(Vector4.Zero) && !_maskTintDirty))
        {
            return;
        }

        _tintModel.ActorTintOffset = Vector3.Zero;
        _tintModel.ActorRim = Vector4.Zero;
        _tintShader.Use();
        _tintShader.SetVector3(TintUniformName, Vector3.Zero);
        _tintShader.SetVector4(RimUniformName, Vector4.Zero);
        if (_tintShader.AlphaMaskVariant is { } maskProgram)
        {
            maskProgram.Use();
            maskProgram.SetVector3(TintUniformName, Vector3.Zero);
            maskProgram.SetVector4(RimUniformName, Vector4.Zero);
            _tintShader.Use();
        }

        _appliedTint = Vector3.Zero;
        _appliedRim = Vector4.Zero;
        _maskTintDirty = false;
    }

    private const string TintUniformName = "uActorTintOffset";
    private const string RimUniformName = "uActorRim";
    private const string RimEyeUniformName = "uActorRimEye";

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
