using OpenTK.Mathematics;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.Sky;
using Dream.Simulation.Enemies;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    // The effects pass (particles and light halos). The renderer is created in the constructor
    // with the other GL resources and disposed with the scene; the simulation, the flash lights,
    // the contact shadows and the bridge that feeds them combat events are plain CPU state. All of
    // them stay null when the renderer could not be created (missing atlas, failed shader), and the
    // scene then runs without effects: no particles, no flashes, no shadows.
    private FxRenderer? _fxRenderer;
    private FxSimulation? _fxSimulation;
    private FxFlashList? _fxFlashes;
    private FxShadowList? _fxShadows;
    private FxEventBridge? _fxBridge;
    private readonly FxInstance[] _fxInstances = new FxInstance[FxLimits.MaxInstances];

    // The remote players of the frame being drawn (CollectEffectLights stores them, the effects
    // pass reads them), and the night factor the last effects pass used for the flash lights.
    private IReadOnlyList<RemotePlayerPresentationState>? _fxRemotePlayers;
    private float _fxLastNight;

    // Appends the flash lights after the held lights and lamps, so a flash never takes a slot from
    // either (see FxFlashList.AppendTo).
    partial void CollectEffectLights(LightCollector lights, IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        _fxRemotePlayers = remotePlayers;
        _fxFlashes?.AppendTo(lights, _fxLastNight);
    }

    // Runs right after the sky, in its own GPU section after the Actors section: the opaque world is
    // done, so the depth buffer is final, and the HUD has not started. Advances the effects by the
    // frame time, writes the instances and draws them in its own timed section. Does nothing when
    // the scene has no effects.
    private void DrawEffectsPass(in SkyState sky, in WorldSessionPresentationState presentation, GpuFrameTimer gpuTimer, float deltaSeconds)
    {
        if (_fxRenderer is not { } renderer || _fxSimulation is not { } simulation
            || _fxFlashes is not { } flashes || _fxShadows is not { } shadows)
        {
            return;
        }

        Matrix4 view = presentation.Camera.GetViewMatrix();
        FxViewBasis basis = FxViewBasis.FromView(in view);
        FxFrameInputs inputs = new(
            deltaSeconds,
            presentation.Camera.Position,
            presentation.Camera.Front,
            _runtime.Player.GetWorldPosition(),
            sky.StarVisibility,
            basis);
        _fxLastNight = sky.StarVisibility;
        flashes.Advance(deltaSeconds);
        simulation.Advance(in inputs, _render.Lights.Lights);

        CollectShadows(shadows, in presentation);
        int shadowCount = shadows.Write(_fxInstances, presentation.Camera.Position, FxSimulation.ViewCentre(in inputs));
        int count = simulation.WriteInstances(_fxInstances, shadowCount);

        gpuTimer.BeginSection(GpuTimerSection.Effects);
        renderer.Draw(_fxInstances, count, in basis, view * _render.ProjectionMatrix);
        gpuTimer.EndSection();
    }

    // The players probe the map for the ground under their feet (the pose is the capsule centre,
    // half a metre above them); enemies keep to their own pose height.
    private void CollectShadows(FxShadowList shadows, in WorldSessionPresentationState presentation)
    {
        shadows.Clear();
        AddPlayerShadow(shadows, _runtime.Player.GetWorldPosition());

        IReadOnlyList<RemotePlayerPresentationState>? remotePlayers = _fxRemotePlayers;
        for (int i = 0; remotePlayers != null && i < remotePlayers.Count; i++)
        {
            RemotePlayerPresentationState remote = remotePlayers[i];
            if (remote.IsAlive)
            {
                AddPlayerShadow(shadows, remote.Pose.Position);
            }
        }

        IReadOnlyList<EnemyActor> enemies = presentation.ActiveEnemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyActor actor = enemies[i];
            Vector3 position = EnemyRenderer.ResolveRenderPose(actor, _scenePresenter, RemotePlayerRenderTick).Position;
            shadows.Add(position, position.Y, actor.CollisionRadius * FxShadowList.EnemyRadiusScale);
        }
    }

    private void AddPlayerShadow(FxShadowList shadows, Vector3 pose)
    {
        Vector3 feet = pose - Vector3.UnitY * 0.5f;
        if (FxGroundProbe.TryFindGroundY(_runtime.Map, feet, out float groundY))
        {
            shadows.Add(feet, groundY, FxShadowList.PlayerRadius);
        }
    }

    private void DisposeEffects()
    {
        // The bridge goes first so no event reaches a simulation whose renderer is gone.
        _fxBridge?.Dispose();
        _fxBridge = null;
        _fxSimulation = null;
        _fxFlashes = null;
        _fxShadows = null;
        _fxRemotePlayers = null;
        _fxRenderer?.Dispose();
        _fxRenderer = null;
    }
}
