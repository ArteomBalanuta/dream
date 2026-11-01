using OpenTK.Mathematics;
using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Rendering.Sky;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    // The effects pass (particles and light halos). The renderer is created in the constructor
    // with the other GL resources and disposed with the scene; the simulation and the bridge that
    // feeds it combat events are plain CPU state.
    private FxRenderer? _fxRenderer;
    private FxSimulation? _fxSimulation;
    private FxEventBridge? _fxBridge;
    private readonly FxInstance[] _fxInstances = new FxInstance[FxLimits.MaxInstances];

    // Runs right after the sky, inside the Actors GPU section's tail: the opaque world is done, so
    // the depth buffer is final, and the HUD has not started. Advances the effects by the frame
    // time, writes the instances and draws them in its own timed section.
    private void DrawEffectsPass(in SkyState sky, in WorldSessionPresentationState presentation, GpuFrameTimer gpuTimer, float deltaSeconds)
    {
        FxRenderer renderer = _fxRenderer ?? throw new ObjectDisposedException(nameof(PlayScene));
        FxSimulation simulation = _fxSimulation ?? throw new ObjectDisposedException(nameof(PlayScene));

        Matrix4 view = presentation.Camera.GetViewMatrix();
        FxFrameInputs inputs = new(
            deltaSeconds,
            presentation.Camera.Position,
            presentation.Camera.Front,
            _runtime.Player.GetWorldPosition(),
            sky.StarVisibility);
        simulation.Advance(in inputs, _render.Lights.Lights);
        int count = simulation.WriteInstances(_fxInstances);

        gpuTimer.BeginSection(GpuTimerSection.Effects);
        FxViewBasis basis = FxViewBasis.FromView(in view);
        renderer.Draw(_fxInstances, count, in basis, view * _render.ProjectionMatrix);
        gpuTimer.EndSection();
    }

    private void DisposeEffects()
    {
        // The bridge goes first so no event reaches a simulation whose renderer is gone.
        _fxBridge?.Dispose();
        _fxBridge = null;
        _fxSimulation = null;
        _fxRenderer?.Dispose();
        _fxRenderer = null;
    }
}
