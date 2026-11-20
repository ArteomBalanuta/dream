using Dream.Rendering.Core;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.CameraControl;

// Turns raw mouse, keys and window events into CameraInputFrames. Collect runs every Update and accumulates;
// Take runs once per rendered frame and resets the accumulators. Reads only the real mouse.
public sealed class CameraInputSampler
{
    // The few raw inputs the sampler reads; the public Collect maps OpenTK state onto it.
    internal readonly record struct RawInput(
        float MouseX,
        float ScrollY,
        bool MiddleDown,
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool Comma,
        bool Period,
        bool Home,
        bool Y);

    private TacticalCameraSettings _settings;
    private bool _focused;
    private bool _inside;
    private double _focusGainedAt = double.NegativeInfinity;
    private double _enteredAt = double.NegativeInfinity;
    private double _dwellStart = double.NaN;
    private bool _dragLatched;
    private bool _prevMiddle;
    private float _lastX;
    private bool _prevComma, _prevPeriod, _prevHome, _prevY;

    private Vector2 _edgePan;
    private Vector2 _keyPan;
    private float _dragPixels;
    private bool _dragReleased;
    private int _rotateSteps;
    private float _zoomNotches;
    private bool _recentre;
    private bool _toggleAnchor;

    public CameraInputSampler(TacticalCameraSettings settings)
    {
        _settings = (settings ?? TacticalCameraSettings.Default).Sanitized();
    }

    public TacticalCameraSettings Settings
    {
        get => _settings;
        set => _settings = (value ?? TacticalCameraSettings.Default).Sanitized();
    }

    public bool RotateDragHeld => _dragLatched;

    public void OnFocusChanged(bool focused, double nowSeconds)
    {
        _focused = focused;
        if (focused)
        {
            _focusGainedAt = nowSeconds;
        }
        else
        {
            ReleaseDrag();
        }
    }

    public void OnCursorEnterLeave(bool inside, double nowSeconds)
    {
        _inside = inside;
        if (inside)
        {
            _enteredAt = nowSeconds;
        }
    }

    public void Collect(
        MouseState mouse,
        KeyboardState keys,
        Vector2i clientSize,
        float uiLayoutScale,
        in WorldPointerState pointer,
        CameraGate gate,
        double nowSeconds)
    {
        RawInput raw = new(
            mouse.X,
            mouse.ScrollDelta.Y,
            mouse.IsButtonDown(MouseButton.Middle),
            keys.IsKeyDown(Keys.Up),
            keys.IsKeyDown(Keys.Down),
            keys.IsKeyDown(Keys.Left),
            keys.IsKeyDown(Keys.Right),
            keys.IsKeyDown(Keys.Comma),
            keys.IsKeyDown(Keys.Period),
            keys.IsKeyDown(Keys.Home),
            keys.IsKeyDown(Keys.Y));
        Collect(raw, clientSize, uiLayoutScale, pointer, gate, nowSeconds);
    }

    internal void Collect(
        in RawInput raw,
        Vector2i clientSize,
        float uiLayoutScale,
        in WorldPointerState pointer,
        CameraGate gate,
        double nowSeconds)
    {
        bool mouseOk = gate.Open && !gate.ScriptedPointer;

        if (!mouseOk)
        {
            ReleaseDrag();
        }
        else
        {
            if (raw.MiddleDown && !_prevMiddle && _focused && !pointer.OverHud)
            {
                _dragLatched = true;
                _lastX = raw.MouseX;
            }

            if (_dragLatched)
            {
                if (raw.MiddleDown)
                {
                    _dragPixels += raw.MouseX - _lastX;
                }
                else
                {
                    ReleaseDrag();
                }
            }

            if (_focused && !pointer.OverHud && raw.ScrollY != 0f)
            {
                float notches = raw.ScrollY;
                _zoomNotches += _settings.InvertZoom ? -notches : notches;
            }
        }

        _lastX = raw.MouseX;
        _prevMiddle = raw.MiddleDown;

        UpdateEdge(mouseOk, clientSize, uiLayoutScale, pointer, gate, nowSeconds);

        if (gate.KeysOpen)
        {
            Vector2 pan = new(
                (raw.Right ? 1f : 0f) - (raw.Left ? 1f : 0f),
                (raw.Up ? 1f : 0f) - (raw.Down ? 1f : 0f));
            if (pan.LengthSquared > 1f)
            {
                pan.Normalize();
            }

            _keyPan = pan;
            if (raw.Comma && !_prevComma)
            {
                _rotateSteps--;
            }

            if (raw.Period && !_prevPeriod)
            {
                _rotateSteps++;
            }

            _recentre |= raw.Home && !_prevHome;
            _toggleAnchor |= raw.Y && !_prevY;
        }
        else
        {
            _keyPan = Vector2.Zero;
        }

        _prevComma = raw.Comma;
        _prevPeriod = raw.Period;
        _prevHome = raw.Home;
        _prevY = raw.Y;
    }

    private void UpdateEdge(bool mouseOk, Vector2i size, float scale, in WorldPointerState pointer, CameraGate gate, double now)
    {
        Vector2 field = Vector2.Zero;
        Vector2 p = pointer.Position;
        bool eligible = mouseOk && _settings.EdgePanEnabled && _focused && _inside && !gate.HudDragActive && !_dragLatched &&
            float.IsFinite(p.X) && float.IsFinite(p.Y) && p.X >= 0f && p.Y >= 0f && p.X < size.X && p.Y < size.Y &&
            now - Math.Max(_focusGainedAt, _enteredAt) >= _settings.EdgeFocusGraceSeconds;
        if (eligible)
        {
            float s = float.IsFinite(scale) && scale > 0f ? scale : 1f;
            field = EdgePanField.Evaluate(p, size, _settings.EdgeMarginPixels * s);
        }

        if (field == Vector2.Zero)
        {
            _dwellStart = double.NaN;
            _edgePan = Vector2.Zero;
            return;
        }

        if (double.IsNaN(_dwellStart))
        {
            _dwellStart = now;
        }

        _edgePan = gate.Confined || now - _dwellStart >= _settings.EdgeDwellSeconds ? field : Vector2.Zero;
    }

    private void ReleaseDrag()
    {
        if (_dragLatched)
        {
            _dragLatched = false;
            _dragReleased = true;
        }
    }

    public CameraInputFrame Take()
    {
        CameraInputFrame frame = new(
            _edgePan,
            _keyPan,
            _dragPixels,
            _dragLatched,
            _dragReleased,
            _rotateSteps,
            _zoomNotches,
            _recentre,
            _toggleAnchor);
        _dragPixels = 0f;
        _dragReleased = false;
        _rotateSteps = 0;
        _zoomNotches = 0f;
        _recentre = false;
        _toggleAnchor = false;
        return frame;
    }
}
