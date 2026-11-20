using OpenTK.Mathematics;
using Dream.Application;
using Dream.Client.CameraControl;
using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Mist;
using Dream.Simulation.World;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    // The war mist: a drifting veil over ground the player is far from, thinner where they have
    // been. Isometric only, client side and visual only. The renderer is created on the first
    // draw (on the render thread, after the effects) and not before, and a failure to create it
    // turns the mist off for the rest of the scene with one line in the render fault log. What
    // the player has seen lives in _exploration for the life of the scene and is never saved.
    private WarMistRenderer? _warMistRenderer;
    private bool _warMistFailed;
    private ExplorationMemory? _exploration;
    private Map? _explorationMap;
    private double _warMistSeconds;

    // The slab's half side: the default, or the view footprint's radius at the farthest zoom on the widest window
    // when that is larger (the slab is axis aligned and the view turns, so the whole disc must be covered).
    internal static readonly float WarMistSlabHalfExtent = MathF.Max(
        WarMistField.HalfExtent,
        TacticalCameraLimits.FootprintRadius(TacticalCameraLimits.MaxDistance, TacticalCameraLimits.ReferenceAspect));

    // Runs at the top of the effects pass, in the Actors GPU section: the opaque world and the
    // actors are drawn, and the effects section has not started. The only on/off gate is
    // _settings.WarMistEnabled (initialised from DREAM_WAR_MIST), so the Settings checkbox works
    // whatever the environment said; exploration keeps accumulating while the veil is off.
    private void DrawWarMistPass(in WorldSessionPresentationState presentation, float deltaSeconds)
    {
        WarMistSettings settings = WarMistSettings.Current;
        if (_warMistFailed || _settings.CameraViewMode != CameraViewMode.Isometric)
        {
            return;
        }

        Map map = _runtime.Map;
        if (_exploration == null || !ReferenceEquals(map, _explorationMap))
        {
            _explorationMap = map;
            if (_exploration == null)
            {
                _exploration = new ExplorationMemory(map.Width, map.Depth);
            }
            else
            {
                _exploration.Reset(map.Width, map.Depth);
            }

            if (settings.ExploredAtStart)
            {
                _exploration.RevealAll();
            }
        }

        Vector3 focus = _runtime.LastCameraFocus;
        _exploration.Reveal(focus.X, focus.Z);
        _warMistSeconds += deltaSeconds;
        if (!_settings.WarMistEnabled)
        {
            return;
        }

        if (_warMistRenderer == null)
        {
            try
            {
                _warMistRenderer = new WarMistRenderer(settings.SliceCount);
            }
            catch (Exception exception)
            {
                _warMistFailed = true;
                RenderFaultLog.Shared.Report("war-mist", exception);
                return;
            }
        }

        // The slab follows the view (a panned camera sees far from the player); the density and clear field stays
        // on the player, so panning never thins the veil anywhere the player has not been.
        _warMistRenderer.Draw(presentation.Camera.GetViewMatrix() * _render.ProjectionMatrix, focus, _runtime.LastViewTarget, WarMistSlabHalfExtent, _warMistSeconds, _exploration);
    }

    private void DisposeWarMist()
    {
        _warMistFailed = true;
        _exploration = null;
        _explorationMap = null;
        _warMistRenderer?.Dispose();
        _warMistRenderer = null;
    }
}
