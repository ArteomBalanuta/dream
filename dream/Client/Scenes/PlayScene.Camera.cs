using Dream.Client.CameraControl;
using Dream.Client.Input;
using Dream.Rendering.Core;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.Scenes;

// The tactical camera's scene side: the rig, the input sampler and the cursor confiner, all created on first use
// (never in field initialisers, so building a PlayScene touches none of them), and the thin glue the other
// PlayScene parts call. The maths live in Dream.Client.CameraControl.
public partial class PlayScene
{
    private TacticalCameraRig? _cameraRig;
    private CameraInputSampler? _cameraSampler;
    private ICursorConfiner? _cameraConfiner;
    private Func<Vector3, TacticalCameraPose>? _cameraPoseDelegate;
    private TacticalCameraSettings? _cameraSettingsApplied;
    private ClickMoveHost? _cameraPickHost;
    private float _cameraFrameSeconds;
    private Vector2 _cameraPointerPixel;
    private readonly CameraConfineApplier _cameraConfine = new();
    private bool _cameraWindowSynced;
    private bool _cameraLastFocused;
    private bool _cameraWindowFocused;
    private bool _cameraLastHovered;

    // True while a rotation drag is held: the cursor shows the Grab sprite.
    public bool CameraRotateDragHeld => _cameraSampler?.RotateDragHeld ?? false;

    // Screen up on the ground for the last rendered pose: the basis of W/A/S/D.
    public Vector3 CurrentCameraGroundForward => _runtime.CurrentGroundForward;

    // The OS-level cursor clip; the platform's own (Win32CursorConfiner on Windows) unless a host sets another.
    public void SetCameraConfiner(ICursorConfiner confiner)
    {
        ArgumentNullException.ThrowIfNull(confiner);
        _cameraConfiner = confiner;
    }

    // Per Update, after the pointer sample: feeds the sampler this frame's mouse, keys and gate.
    // loadingGated is the same flag Update already computes for the loading screen.
    public void UpdateCameraInput(KeyboardState keys, MouseState mouse, in WorldPointerState pointer, bool loadingGated)
    {
        EnsureCamera();
        _cameraPointerPixel = pointer.Position;
        bool open = _windowFocused && !loadingGated && !_settings.PauseMenuVisible && !_settings.SettingsVisible;
        CameraGate gate = new(
            Open: open,
            KeysOpen: open && !_chatInput.IsTyping,
            Confined: _cameraConfiner?.IsConfined ?? false,
            // The classic HUD's item drag; an ImGui drag is not reachable from here (ImGui owns it).
            HudDragActive: _render.ClassicSurfaceForCapture?.Input.Dragging ?? false,
            ScriptedPointer: _hudCapture is not null);

        Vector2i size = _render.ViewportSize;
        float layoutScale = UiScale.Resolve(size.X, size.Y, _settings.UiScaleSetting);
        _cameraSampler!.Collect(mouse, keys, size, layoutScale, in pointer, gate, _clientPresentationTimeSeconds);
        _render.CameraDragging = _cameraSampler.RotateDragHeld;
    }

    // Per Render, before CapturePresentationState: the delegate that turns the player focus into this frame's pose.
    // The delegate is allocated once.
    public Func<Vector3, TacticalCameraPose> CameraPoseForFocus(float frameSeconds)
    {
        EnsureCamera();
        _cameraFrameSeconds = frameSeconds;
        return _cameraPoseDelegate!;
    }

    private TacticalCameraPose AdvanceCamera(Vector3 playerFocus)
    {
        TacticalCameraRig rig = _cameraRig!;
        CameraInputFrame frame = _cameraSampler!.Take();
        if (frame.ZoomNotches != 0f && !rig.ZoomGestureActive)
        {
            rig.LatchZoomAnchor(ResolveZoomAnchor());
        }

        TacticalCameraPose pose = rig.Advance(playerFocus, in frame, _cameraFrameSeconds);
        SyncAnchorModeToSettings(rig);
        return pose;
    }

    // The Y key flips the rig's mode; the Camera options row reads the settings, so the new mode is written back
    // (Game persists it with the rest of the client config). _cameraSettingsApplied follows, so EnsureCamera does
    // not push the same value into the rig again.
    private void SyncAnchorModeToSettings(TacticalCameraRig rig)
    {
        if (rig.AnchorMode == _settings.TacticalCamera.AnchorMode)
        {
            return;
        }

        _settings.SetTacticalCamera(_settings.TacticalCamera with { AnchorMode = rig.AnchorMode });
        _cameraSettingsApplied = _settings.TacticalCamera;
        _cameraSampler!.Settings = _settings.TacticalCamera;
    }

    // The point a zoom gesture keeps under the cursor: the hovered enemy's anchor, else what the ground pick under
    // the cursor hits, else where the cursor ray meets the horizontal plane through the view target, else null
    // (zoom about the view centre).
    private Vector3? ResolveZoomAnchor()
    {
        _cameraPickHost ??= new ClickMoveHost(this);
        if (_cameraPickHost.TryGetEnemyAnchorAt(_cameraPointerPixel, out Vector3 enemyAnchor))
        {
            return enemyAnchor;
        }

        if (_cameraPickHost.TryPick(_cameraPointerPixel, out Dream.Client.Input.ClickPick pick))
        {
            return pick.Point;
        }

        Dream.Simulation.Players.Camera? camera = _render.LastWorldCamera;
        if (camera is not null
            && WorldCursorPicker.TryBuildRay(
                camera.GetViewMatrix(),
                _render.ProjectionMatrix,
                _cameraPointerPixel,
                _render.ViewportSize,
                out Vector3 origin,
                out Vector3 direction)
            && TryIntersectPlaneY(origin, direction, _runtime.LastViewTarget.Y, out Vector3 onPlane))
        {
            return onPlane;
        }

        return null;
    }

    // Where a ray meets the horizontal plane y = planeY, in front of the origin; false when it runs parallel or away.
    internal static bool TryIntersectPlaneY(Vector3 origin, Vector3 direction, float planeY, out Vector3 point)
    {
        point = default;
        if (MathF.Abs(direction.Y) < 1e-5f)
        {
            return false;
        }

        float t = (planeY - origin.Y) / direction.Y;
        if (!float.IsFinite(t) || t < 0f)
        {
            return false;
        }

        point = origin + direction * t;
        return true;
    }

    // Executes a parsed cam: capture step. Each snaps the goal and the current pose, so the frame is deterministic.
    internal void ApplyCaptureCamera(HudCaptureStepSpec step)
    {
        EnsureCamera();
        TacticalCameraRig rig = _cameraRig!;
        Vector3 player = _runtime.LastPlayerFocus;
        switch (step.Verb)
        {
            case HudCaptureVerb.CamYaw:
                rig.SnapTo(player, step.CamValueA, null, null, null);
                break;
            case HudCaptureVerb.CamZoom:
                rig.SnapTo(player, null, step.CamValueA, null, null);
                break;
            case HudCaptureVerb.CamPan:
                rig.SnapTo(player, null, null, new Vector2(step.CamValueA, step.CamValueB), null);
                break;
            case HudCaptureVerb.CamFree:
                rig.SnapTo(player, null, null, null, CameraAnchorMode.Free);
                break;
            case HudCaptureVerb.CamFollow:
                rig.SnapTo(player, null, null, null, CameraAnchorMode.Follow);
                break;
            case HudCaptureVerb.CamReset:
                rig.Reset(player);
                break;
        }
    }

    // The camera part of the capture status line.
    public string CameraStatusText()
    {
        EnsureCamera();
        TacticalCameraRig rig = _cameraRig!;
        TacticalCameraPose pose = rig.Pose;
        Vector3 offset = rig.PanOffset;
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
        return string.Create(
            c,
            $"cam target={pose.Target.X:0.00},{pose.Target.Y:0.00},{pose.Target.Z:0.00} yaw={pose.YawDegrees:0.0} dist={pose.Distance:0.00} offset={offset.X:0.00},{offset.Z:0.00} mode={rig.AnchorMode.ToString().ToLowerInvariant()}");
    }

    // Per frame from the host, before Update and Render: the window focus and whether the OS cursor is over the
    // client area (GLFW's hovered attribute). Only changes reach the sampler, and the first call seeds it, so the
    // cursor leaving for another monitor stops edge pan even if no leave event was ever delivered.
    public void SyncCameraWindowState(bool focused, bool hovered)
    {
        if (!_cameraWindowSynced || focused != _cameraLastFocused)
        {
            OnCameraFocusChanged(focused);
        }

        _cameraWindowFocused = focused;

        if (!_cameraWindowSynced || hovered != _cameraLastHovered)
        {
            OnCameraCursorEnterLeave(hovered);
        }

        _cameraWindowSynced = true;
        _cameraLastFocused = focused;
        _cameraLastHovered = hovered;
    }

    public void OnCameraFocusChanged(bool focused)
    {
        EnsureCamera();
        _cameraSampler!.OnFocusChanged(focused, _clientPresentationTimeSeconds);
    }

    public void OnCameraCursorEnterLeave(bool inside)
    {
        EnsureCamera();
        _cameraSampler!.OnCursorEnterLeave(inside, _clientPresentationTimeSeconds);
    }

    // Per frame: clips the cursor or lets it go per the policy. The focus is the one the host passed to
    // SyncCameraWindowState this frame, not last frame's. clientRectScreen is the client area in screen pixels.
    public void ApplyCursorConfinement(bool minimised, bool fullscreenLike, Box2i clientRectScreen)
    {
        EnsureCamera();
        bool want = CursorConfinePolicy.ShouldConfine(
            _settings.TacticalCamera.ConfineMode,
            _cameraWindowFocused,
            minimised,
            fullscreenLike,
            _settings.PauseMenuVisible || _settings.SettingsVisible,
            LoadingOverlayVisible,
            inPlay: true);
        ICursorConfiner confiner = _cameraConfiner ??= CursorConfiners.CreateForPlatform();
        _cameraConfine.Apply(confiner, want, _cameraWindowFocused, clientRectScreen);
    }

    // Always lets the cursor go: Game calls it from its teardown paths.
    public void ReleaseCameraResources()
    {
        _cameraConfiner?.Release();
        _cameraConfine.Reset();
    }

    private void EnsureCamera()
    {
        if (_cameraRig is null)
        {
            TacticalCameraSettings settings = _settings.TacticalCamera;
            _cameraRig = new TacticalCameraRig(settings);
            _cameraSampler = new CameraInputSampler(settings);
            _cameraSettingsApplied = settings;
            _cameraPoseDelegate = AdvanceCamera;
            return;
        }

        // The Options menu replaces the settings object; both parts follow it.
        TacticalCameraSettings current = _settings.TacticalCamera;
        if (!ReferenceEquals(current, _cameraSettingsApplied))
        {
            _cameraRig.Settings = current;
            _cameraSampler!.Settings = current;
            _cameraSettingsApplied = current;
        }
    }
}

// The decision half of the cursor clip, free of the scene so a test can drive it with a fake confiner. It touches
// the OS only when the wish or the rectangle changes, on every focus-gained edge (another program may have taken
// the clip while the window was away) and whenever the confiner reports the clip is no longer in force.
internal sealed class CameraConfineApplier
{
    private bool _applied;
    private bool _lastFocused;
    private Box2i _rect;

    public void Apply(ICursorConfiner confiner, bool want, bool focused, Box2i clientRectScreen)
    {
        bool focusGained = focused && !_lastFocused;
        _lastFocused = focused;
        if (!want)
        {
            if (_applied)
            {
                confiner.Release();
                _applied = false;
            }

            return;
        }

        if (_applied && !focusGained && clientRectScreen == _rect && confiner.IsConfined)
        {
            return;
        }

        confiner.Confine(clientRectScreen);
        _rect = clientRectScreen;
        _applied = true;
    }

    public void Reset()
    {
        _applied = false;
        _lastFocused = false;
    }
}
