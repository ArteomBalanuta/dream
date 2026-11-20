using Dream.Client.CameraControl;
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
    private bool _cameraConfineApplied;
    private Box2i _cameraConfineRect;

    // True while a rotation drag is held: the cursor shows the Grab sprite.
    public bool CameraRotateDragHeld => _cameraSampler?.RotateDragHeld ?? false;

    // Screen up on the ground for the last rendered pose: the basis of W/A/S/D.
    public Vector3 CurrentCameraGroundForward => _runtime.CurrentGroundForward;

    // The OS-level cursor clip. The host sets the real one (Win32CursorConfiner) before the first frame; without
    // one, confinement does nothing.
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

        return rig.Advance(playerFocus, in frame, _cameraFrameSeconds);
    }

    // The point a zoom gesture keeps under the cursor: the hovered enemy's anchor, else what the ground pick under
    // the cursor hits, else null (zoom about the view centre).
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

        return null;
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

    // Per frame: clips the cursor or lets it go per the policy, touching the OS only when the decision or the
    // rectangle changes. clientRectScreen is the client area in screen pixels.
    public void ApplyCursorConfinement(bool minimised, bool fullscreenLike, Box2i clientRectScreen)
    {
        EnsureCamera();
        bool want = CursorConfinePolicy.ShouldConfine(
            _settings.TacticalCamera.ConfineMode,
            _windowFocused,
            minimised,
            fullscreenLike,
            _settings.SettingsVisible,
            LoadingOverlayVisible,
            inPlay: true);
        if (want == _cameraConfineApplied && (!want || clientRectScreen == _cameraConfineRect))
        {
            return;
        }

        ICursorConfiner confiner = _cameraConfiner ??= new InertCursorConfiner();
        if (want)
        {
            confiner.Confine(clientRectScreen);
            _cameraConfineRect = clientRectScreen;
        }
        else
        {
            confiner.Release();
        }

        _cameraConfineApplied = want;
    }

    // Always lets the cursor go: the Lead calls it from Dispose.
    public void ReleaseCameraResources()
    {
        _cameraConfiner?.Release();
        _cameraConfineApplied = false;
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

    // Stands in until the host supplies a real confiner.
    private sealed class InertCursorConfiner : ICursorConfiner
    {
        public bool IsConfined { get; private set; }

        public void Confine(Box2i clientRectScreen) => IsConfined = false;

        public void Release() => IsConfined = false;
    }
}
