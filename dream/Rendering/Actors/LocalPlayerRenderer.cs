using Dream.Client.Presentation;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.Players;

namespace Dream.Rendering.Actors;

// What the local player's animation needs besides its pose: the held items, and the events and
// movement the play scene knows. The scene sets it once per frame before the actors draw
// (LocalPlayerRenderer.SetFrameInputs); the Draw signature stays the pose alone.
public readonly record struct LocalPlayerFrameInputs(
    PlayerEquipmentVisuals Equipment,
    bool IsAlive,
    float HorizontalSpeed,
    bool Airborne,
    int AttackSerial,
    int HitSerial,
    // The scene's smoothed travel direction (LocalPlayerRenderPose.TravelDirection); zero when not moving.
    Vector3 TravelDirection = default);

public sealed class LocalPlayerRenderer : IDisposable
{
    private const int LocalActorId = 0;

    private readonly Render _render;
    private readonly PlayerAnimationClipTracker _clipTracker = new();
    private readonly PlayerAnimationStateMachine _machine = new();
    private readonly PlayerPoseLayers _layers = new();
    private readonly HeldWeaponRenderer _heldWeapons = new();
    private LocalPlayerFrameInputs _inputs = new(default, true, 0f, false, 0, 0);
    private AnimatedModelInstanceHandle? _model;
    private bool _disposed;

    public LocalPlayerRenderer(Render render)
    {
        _render = render;
    }

    public void SetFrameInputs(in LocalPlayerFrameInputs inputs)
    {
        _inputs = inputs;
    }

    // The animation frame the last Draw selected, and the raw travel-minus-facing delta it was
    // selected from. The capture's status line logs them (acceptance M6): without this the chosen
    // clip is nowhere in the output, so "the state machine selects by C2" could only be DERIVED
    // from the logged axes plus a reading of ResolveWalkClip - which would not catch a machine that
    // computed the right sector and then played the wrong clip. Null until the player has been
    // drawn (Draw runs in third person only, and not before the model is uploaded).
    public PlayerAnimationFrame? LastAnimationFrame { get; private set; }

    public float LastRawLegTwistRadians { get; private set; }

    public void Draw(LocalPlayerPresentationState localPlayer, Camera camera, float deltaTime, bool silhouette = false)
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

        // Moving comes from the render pose (tick speed with hysteresis, see
        // LocalPlayerRenderSmoother); how fast is the body's, when it reports any.
        float speed = !localPlayer.IsMoving
            ? 0f
            : _inputs.HorizontalSpeed > PlayerAnimationStateMachine.MovingSpeedThreshold
                ? _inputs.HorizontalSpeed
                : PlayerAnimationStateMachine.ReferenceWalkSpeed;
        PlayerEquipmentVisuals equipment = _inputs.Equipment;
        PlayerAnimationInputs animationInputs = new(
            _inputs.IsAlive,
            speed,
            _inputs.Airborne,
            _inputs.AttackSerial,
            _inputs.HitSerial,
            equipment.MainHand is not null,
            equipment.OffHand is not null,
            WeaponMeshCatalog.IsRanged(equipment.MainHand),
            PlayerAnimationStateMachine.LegTwist(
                localPlayer.Facing.X, localPlayer.Facing.Z, _inputs.TravelDirection.X, _inputs.TravelDirection.Z));
        PlayerAnimationFrame frame = _machine.Update(in animationInputs, deltaTime);
        LastAnimationFrame = frame;
        LastRawLegTwistRadians = animationInputs.LegTwistRadians;
        _clipTracker.ApplyFrame(LocalActorId, in frame, instance);
        instance.Update(deltaTime);
        _layers.Update(instance, in frame);

        Shader shader = _render.GetShader(renderState.ShaderName);
        if (silhouette)
        {
            // Silhouette first: the parts of the player the world hides, bright and flat enough to
            // read. Depth is not written, so the normal pass below still sorts itself.
            GL.DepthFunc(DepthFunction.Greater);
            GL.DepthMask(false);
            shader.Use();
            shader.SetVector3("uActorTintOffset", new Vector3(0.5f, 2f, 6f));
            model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: true);
            model.RenderPrepared(shader, instance, renderState.ModelMatrix, _layers.Overrides);
            model.EndRender();
            _heldWeapons.Draw(shader, camera, _render.ProjectionMatrix, instance, _layers, in equipment, renderState.ModelMatrix);
            shader.Use();
            shader.SetVector3("uActorTintOffset", Vector3.Zero);
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Less);
        }

        bool flashing = frame.HitFlinch > 0.01f;
        if (flashing)
        {
            shader.Use();
            shader.SetVector3("uActorTintOffset", PlayerPoseLayers.HitFlashOffset(frame.HitFlinch));
        }

        model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
        model.RenderPrepared(shader, instance, renderState.ModelMatrix, _layers.Overrides);
        model.EndRender();
        _heldWeapons.Draw(shader, camera, _render.ProjectionMatrix, instance, _layers, in equipment, renderState.ModelMatrix);
        if (flashing)
        {
            shader.Use();
            shader.SetVector3("uActorTintOffset", Vector3.Zero);
        }
    }

    // The player's instance handle is a per-instance request, so this renderer owns it: disposing
    // it drops the instance. Draw never requests after Dispose. The shared ModelAssets player
    // model is never disposed here.
    public void Dispose()
    {
        _disposed = true;
        AnimatedModelInstanceHandle? model = _model;
        _model = null;
        _heldWeapons.Dispose();
        model?.Dispose();
    }
}
