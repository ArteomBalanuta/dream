using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// What a menu button did, for sound. Rendering never references the audio code: whoever plays sound
// (GameAudio.EnsureStarted) sets UiInputRouter.Feedback once.
public enum UiFeedback
{
    Hover,
    Click
}

// Immediate-mode input for the classic HUD. Panels call Button, Hovered, RightClicked and BeginDrag
// while they build; the answer comes from this frame's pointer and the hot id the previous frame
// left (the last widget under the pointer, so a widget built later sits on top). One instance is
// reused: nothing here allocates per frame. Call BeginFrame before the panels and EndFrame after.
public sealed class UiInputRouter : IUiInput
{
    public const int NoId = int.MinValue;

    // Called on the render thread when a Button is clicked, and once when the pointer enters a Button.
    public static Action<UiFeedback>? Feedback { get; set; }

    // How far the pointer moves with the button down before a press becomes a drag.
    public const float DragThresholdPixels = 4f;

    private UiPointer _pointer;
    private bool _wasInteractive;
    private bool _prevLeft;
    private bool _prevRight;
    private bool _leftPressed;
    private bool _leftReleased;
    private bool _rightReleased;
    private int _hot = NoId;
    private int _nextHot = NoId;
    private int _pressId = NoId;
    private int _rightPressId = NoId;
    private Vector2 _pressPosition;
    private bool _modalPrevFrame;
    private bool _modalActive;
    private int _dragPayload = -1;
    private bool _dragReleased;
    private Vector2 _dropPosition;
    private int _hoverFeedbackId = NoId;

    // Per-instance override of the static Feedback (tests, a second UI that wants its own sound).
    public Action<UiFeedback>? LocalFeedback { get; set; }

    private void Notify(UiFeedback feedback) => (LocalFeedback ?? Feedback)?.Invoke(feedback);

    public UiPointer Pointer => _pointer;

    // The widget under the pointer as of the previous frame, or NoId.
    public int HotId => _hot;

    public int ActiveDragPayload => _dragPayload;

    public bool Dragging => _dragPayload >= 0;

    // Set by a panel that draws a modal layer (menu, confirm). Reset every frame, so the panel sets
    // it again each frame; while the previous frame was modal, widgets built before the panel sets
    // it again cannot become hot.
    public bool ModalActive
    {
        get => _modalActive;
        set => _modalActive = value;
    }

    public void BeginFrame(UiPointer pointer)
    {
        bool interactive = pointer.Interactive;
        _pointer = pointer;
        _modalPrevFrame = _modalActive;
        _modalActive = false;
        _hot = _nextHot;
        _nextHot = NoId;

        if (!interactive)
        {
            // Not interactive: nothing is hot, pressed or dragged, and a button still held when the
            // UI becomes interactive again is not a new press.
            _hot = NoId;
            _pressId = NoId;
            _rightPressId = NoId;
            _leftPressed = false;
            _leftReleased = false;
            _rightReleased = false;
            _prevLeft = pointer.LeftDown;
            _prevRight = pointer.RightDown;
            _wasInteractive = false;
            ClearDrag();
            return;
        }

        if (!_wasInteractive)
        {
            _prevLeft = pointer.LeftDown;
            _prevRight = pointer.RightDown;
        }

        _wasInteractive = true;
        _leftPressed = pointer.LeftDown && !_prevLeft;
        _leftReleased = !pointer.LeftDown && _prevLeft;
        bool rightPressed = pointer.RightDown && !_prevRight;
        _rightReleased = !pointer.RightDown && _prevRight;
        _prevLeft = pointer.LeftDown;
        _prevRight = pointer.RightDown;

        if (_leftPressed)
        {
            _pressId = _hot;
            _pressPosition = pointer.Position;
        }

        if (rightPressed)
        {
            _rightPressId = _hot;
        }

        _dragReleased = Dragging && _leftReleased;
        if (_dragReleased)
        {
            _dropPosition = pointer.Position;
        }
    }

    // Drops a drag nobody ended and forgets the press once the button is up.
    public void EndFrame()
    {
        if (_dragReleased)
        {
            ClearDrag();
        }

        if (_leftReleased)
        {
            _pressId = NoId;
        }

        if (_rightReleased)
        {
            _rightPressId = NoId;
        }
    }

    // F3 and a changed inventory snapshot call this. Nothing is dropped anywhere, and the press is
    // forgotten so a button still held cannot start the same drag again on the new snapshot.
    public void CancelDrag()
    {
        ClearDrag();
        _pressId = NoId;
    }

    public bool Hovered(int id, UiRect r)
    {
        Register(id, r);
        return _hot == id;
    }

    // A click on release over the widget that took the press. A press that turned into a drag is
    // not a click.
    public bool Button(int id, UiRect r)
    {
        Register(id, r);
        if (_hot == id && !IsSuppressed(id) && _hoverFeedbackId != id)
        {
            _hoverFeedbackId = id;
            Notify(UiFeedback.Hover);
        }
        else if (_hot != id && _hoverFeedbackId == id)
        {
            _hoverFeedbackId = NoId;
        }

        bool clicked = _leftReleased && !_dragReleased && _pressId == id && _hot == id && !IsSuppressed(id);
        if (clicked)
        {
            Notify(UiFeedback.Click);
        }

        return clicked;
    }

    public bool RightClicked(int id, UiRect r)
    {
        Register(id, r);
        return _rightReleased && _rightPressId == id && _hot == id && !IsSuppressed(id);
    }

    // True once, on the frame the press on this widget moves past the drag threshold.
    public bool BeginDrag(int id, UiRect r, int payload)
    {
        Register(id, r);
        if (Dragging || payload < 0 || !_pointer.Interactive || !_pointer.LeftDown || _pressId != id)
        {
            return false;
        }

        if (IsSuppressed(id))
        {
            return false;
        }

        Vector2 moved = _pointer.Position - _pressPosition;
        if (moved.LengthSquared < DragThresholdPixels * DragThresholdPixels)
        {
            return false;
        }

        _dragPayload = payload;
        return true;
    }

    // True once, on the frame the button is released over a drag; dropPosition is where.
    public bool TryEndDrag(out int payload, out Vector2 dropPosition)
    {
        if (_dragReleased && Dragging)
        {
            payload = _dragPayload;
            dropPosition = _dropPosition;
            ClearDrag();
            return true;
        }

        payload = -1;
        dropPosition = default;
        return false;
    }

    private void Register(int id, UiRect r)
    {
        if (!_pointer.Interactive || !r.Contains(_pointer.Position))
        {
            return;
        }

        // While the previous frame had a modal layer, only widgets built after the modal panel set
        // ModalActive this frame may take the pointer.
        if (_modalPrevFrame && !_modalActive)
        {
            return;
        }

        _nextHot = id;
    }

    // A widget under a modal layer (built before the layer set ModalActive) never clicks.
    private bool IsSuppressed(int id) => _modalPrevFrame && !_modalActive;

    private void ClearDrag()
    {
        _dragPayload = -1;
        _dragReleased = false;
    }
}

// Synthetic pointer for the capture script (DREAM_HUD_CAPTURE_SCRIPT). The renderer reads it only
// when one is set, which only the capture script does; real input is untouched otherwise.
internal sealed class UiScriptedPointer
{
    public bool Active { get; set; }

    public Vector2 Position { get; set; }

    public bool LeftDown { get; set; }

    public bool RightDown { get; set; }
}
