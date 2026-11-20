using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover;

// The hover lifecycle, pure and driven by dt: Current's enter/stay/exit and the info panel's debounced view
// of it (dwell, warm swap, linger, defeated hold, gate). See the design table 4.4.
public sealed class HoverTracker
{
    private const float Epsilon = 1e-4f;

    private enum PanelMode : byte
    {
        Hidden,
        Live,
        Linger,
        FadeOut,
        Defeated
    }

    private HoverTargetId _current;
    private float _dwell;
    private PanelMode _mode;
    private HoverTargetId _panelTarget;
    private float _alpha;
    private float _timer;

    public HoverFrame Frame { get; private set; }

    // The panel's current target (None while hidden); WorldHover uses it to tell the tracker it is gone.
    public HoverTargetId PanelTarget => _mode == PanelMode.Hidden ? HoverTargetId.None : _panelTarget;

    // gated: a gate closed, hide everything at once. panelTargetGone: the panel's target no longer exists.
    public HoverFrame Advance(HoverTargetId current, Vector3 anchor, HoverOrigin origin, float dt, bool panelTargetGone, bool gated)
    {
        if (gated || current.IsNone)
        {
            current = HoverTargetId.None;
            anchor = default;
            origin = HoverOrigin.None;
        }

        HoverTargetId previous = _current;
        HoverPhase phase;
        if (current == previous)
        {
            phase = current.IsNone ? HoverPhase.None : HoverPhase.Stay;
            _dwell = current.IsNone ? 0f : _dwell + dt;
        }
        else
        {
            phase = current.IsNone ? HoverPhase.Exited : HoverPhase.Entered;
            _dwell = 0f;
        }

        _current = current;
        if (gated)
        {
            ResetPanel();
        }
        else
        {
            UpdatePanel(current, dt, panelTargetGone);
        }

        Frame = new HoverFrame(current, previous, phase, anchor, origin, PanelTarget, _mode == PanelMode.Hidden ? 0f : _alpha, _dwell);
        return Frame;
    }

    // Drops everything at once; the next Advance sees no previous target.
    public void Clear()
    {
        _current = HoverTargetId.None;
        _dwell = 0f;
        ResetPanel();
        Frame = default;
    }

    private void ResetPanel()
    {
        _mode = PanelMode.Hidden;
        _panelTarget = HoverTargetId.None;
        _alpha = 0f;
        _timer = 0f;
    }

    private void UpdatePanel(HoverTargetId current, float dt, bool gone)
    {
        if (!current.IsNone)
        {
            if (_mode == PanelMode.Hidden)
            {
                if (_dwell >= HoverTuning.PanelDwellSeconds - Epsilon)
                {
                    _panelTarget = current;
                    _mode = PanelMode.Live;
                    _alpha = MathF.Min(1f, dt / HoverTuning.PanelFadeInSeconds);
                    _timer = 0f;
                }
            }
            else if (_panelTarget != current)
            {
                // Warm swap: a visible panel follows the cursor to the next target at once.
                _panelTarget = current;
                _mode = PanelMode.Live;
                _alpha = 1f;
                _timer = 0f;
            }
            else
            {
                // Back on the shown target (cancels a linger or fade); fade in if not yet opaque.
                _mode = PanelMode.Live;
                _alpha = MathF.Min(1f, _alpha + (dt / HoverTuning.PanelFadeInSeconds));
            }

            return;
        }

        switch (_mode)
        {
            case PanelMode.Live:
                _timer = 0f;
                if (gone)
                {
                    _mode = PanelMode.Defeated;
                    _alpha = 1f;
                }
                else
                {
                    _mode = PanelMode.Linger;
                }

                break;
            case PanelMode.Linger:
                if (gone)
                {
                    _mode = PanelMode.Defeated;
                    _alpha = 1f;
                    _timer = 0f;
                    break;
                }

                _timer += dt;
                if (_timer >= HoverTuning.LingerSeconds - Epsilon)
                {
                    _mode = PanelMode.FadeOut;
                }

                break;
            case PanelMode.Defeated:
                _timer += dt;
                if (_timer >= HoverTuning.DefeatedHoldSeconds - Epsilon)
                {
                    _mode = PanelMode.FadeOut;
                }

                break;
            case PanelMode.FadeOut:
                _alpha -= dt / HoverTuning.FadeOutSeconds;
                if (_alpha <= 0f)
                {
                    ResetPanel();
                }

                break;
        }
    }
}
