using System.Diagnostics;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// The seam between the GL-free surface and the foundation workstream's GL layer (atlas store,
// UiBatch, submit). Render.CreateClassicHudBackend returns the one implementation.
public interface IClassicHudBackend : IDisposable
{
    ClassicHudBackendState State { get; }

    // One line for the fault log when State is Failed.
    string? FailureReason { get; }

    // Valid when State is Ready.
    UiSpriteTable? Sprites { get; }

    // Starts a frame for this layout (viewport, texel scale, text scale) and returns the canvas the
    // panels draw into. CPU only, no GL calls.
    IUiCanvas BeginFrame(in UiLayout layout);

    // Uploads and draws what the canvas collected, then restores the GL state it changed.
    void Submit();
}

// What the surface needs from the renderer for one frame. A plain readonly struct, passed by in.
public readonly struct ClassicHudFrame
{
    public int Width { get; init; }

    public int Height { get; init; }

    // GameSettings.UiScaleSetting: 0 is auto.
    public float ConfiguredScale { get; init; }

    public UiPointer Pointer { get; init; }

    public HudStatusSnapshot Status { get; init; }

    public PlayerInventorySnapshot? Inventory { get; init; }

    public bool InventoryOpen { get; init; }

    public Vector3 PlayerPosition { get; init; }

    public string? RejectionMessage { get; init; }

    public WorldTransferHudState WorldTransfer { get; init; }

    public LootFeedbackSystem? Loot { get; init; }

    public IReadOnlyList<FloatingCombatTextRenderState>? CombatText { get; init; }

    public IReadOnlyList<HudWorldLabel>? Labels { get; init; }
}

// The timing of the liquid gems, pure and allocation free. The surface advances the eased levels and the
// clock once a frame; the status bar derives the surface phase and the glint sweep from the clock.
public static class GemAnimation
{
    // A frame longer than this (a stall, a breakpoint, a window drag) moves the animation by this much.
    public const float MaxFrameSeconds = 0.1f;

    // Exponential approach rate in 1/s: the drawn level closes about 99.9% of a change in one second.
    public const float EaseRate = 7f;

    // The shown level is snapped to its target inside this distance, so a gem comes to rest exactly.
    public const float SnapDistance = 1f / 512f;

    // The gem pulses only below this health fraction, fully from PulseFullBelow down.
    public const float PulseBelow = 0.25f;
    public const float PulseFullBelow = 0.10f;
    public const float PulseHertz = 1.25f;

    // AnimationSeconds wraps here. Every period below divides it, so the loop has no visible seam.
    public const float LoopSeconds = 840f;
    public const float PhaseHertz = 0.4f;
    public const float SweepPeriodSeconds = 7f;
    public const float SweepDurationSeconds = 1.4f;

    // A frame time as the animation sees it: 0..MaxFrameSeconds, NaN and negative as 0.
    public static float ClampDelta(float seconds) =>
        float.IsNaN(seconds) || seconds <= 0f ? 0f : MathF.Min(seconds, MaxFrameSeconds);

    // One step of the drawn level toward its target. Never leaves 0..1 and never overshoots the target.
    public static float Ease(float current, float target, float deltaSeconds)
    {
        target = float.IsNaN(target) ? 0f : Math.Clamp(target, 0f, 1f);
        current = float.IsNaN(current) ? target : Math.Clamp(current, 0f, 1f);
        float step = current + (target - current) * (1f - MathF.Exp(-EaseRate * ClampDelta(deltaSeconds)));
        return MathF.Abs(target - step) < SnapDistance ? target : step;
    }

    // The low-health pulse, 0..1: nothing at or above PulseBelow, a slow throb that deepens as the
    // level drops. level is the drawn (eased) health fraction.
    public static float Pulse(float level, float seconds)
    {
        if (float.IsNaN(level) || level >= PulseBelow)
        {
            return 0f;
        }

        float strength = Math.Clamp((PulseBelow - level) / (PulseBelow - PulseFullBelow), 0f, 1f);
        float wave = 0.5f - 0.5f * MathF.Cos(MathF.Tau * PulseHertz * seconds);
        return strength * wave;
    }

    // The liquid surface phase, 0..1, repeating.
    public static float Phase(float seconds) => Fraction(seconds * PhaseHertz);

    // The glint sweep position, 0..1 while a sweep runs and 0 between sweeps (the band is off the stone
    // at both ends). offsetSeconds shifts one gem against the other.
    public static float Sweep(float seconds, float offsetSeconds)
    {
        float t = (seconds + offsetSeconds) % SweepPeriodSeconds;
        if (t < 0f)
        {
            t += SweepPeriodSeconds;
        }

        return t >= SweepDurationSeconds || float.IsNaN(t) ? 0f : t / SweepDurationSeconds;
    }

    private static float Fraction(float x) => float.IsFinite(x) ? x - MathF.Floor(x) : 0f;
}

// Builds and submits one classic HUD frame: fills the view, runs the panels against the backend's
// canvas and collects what the player picked. GL-free; the backend does all the GL. Panels send
// their choices here through IHudActions; the renderer turns them into a HudInteractionRequest.
public sealed class ClassicHudSurface : IHudActions, IDisposable
{
    private readonly IClassicHudBackend _backend;
    private readonly HudView _view = new();
    private readonly Func<double> _seconds;
    private HudViewBuilder? _builder;
    private InventoryHudAction? _action;
    private bool _closeInventory;
    private bool _gemsStarted;
    private double _lastSeconds;
    private double _animationSeconds;

    public ClassicHudSurface(IClassicHudBackend backend)
        : this(backend, static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)
    {
    }

    // seconds is the wall clock the gem animation steps by (tests drive it by hand).
    public ClassicHudSurface(IClassicHudBackend backend, Func<double> seconds)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _seconds = seconds ?? throw new ArgumentNullException(nameof(seconds));
    }

    public IClassicHudBackend Backend => _backend;

    public UiInputRouter Input { get; } = new();

    public HudView View => _view;

    public ClassicHudBackendState State => _backend.State;

    // False until the atlas is ready, so a loading frame draws nothing.
    public bool Draw(in ClassicHudFrame frame)
    {
        UiSpriteTable? sprites = _backend.Sprites;
        if (_backend.State != ClassicHudBackendState.Ready || sprites is null)
        {
            return false;
        }

        _builder ??= new HudViewBuilder(sprites, _view);
        _builder.Refresh(frame.Status, frame.Inventory);
        if (_builder.InventoryChanged)
        {
            // The snapshot changed under a drag: the dragged slot may be a different item now.
            Input.CancelDrag();
        }

        HudView v = _view;
        AnimateGems(v);
        v.PlayerPosition = frame.PlayerPosition;
        v.RejectionMessage = frame.RejectionMessage;
        v.WorldTransfer = frame.WorldTransfer;
        v.Loot = frame.Loot;
        v.InventoryOpen = frame.InventoryOpen;
        v.CombatText = frame.CombatText ?? Array.Empty<FloatingCombatTextRenderState>();
        v.Labels = frame.Labels ?? Array.Empty<HudWorldLabel>();

        UiLayout layout = UiLayout.Create(frame.Width, frame.Height, frame.ConfiguredScale);
        Input.BeginFrame(frame.Pointer);
        IUiCanvas canvas = _backend.BeginFrame(in layout);
        ClassicHud.Build(canvas, Input, v, in layout, sprites, this);
        Input.EndFrame();
        _backend.Submit();
        return true;
    }

    // Eases the two gem levels toward their targets and advances the animation clock, from the wall clock
    // capped at GemAnimation.MaxFrameSeconds a frame. The first frame shows the targets as they are, so a
    // gem does not fill up from empty at start-up. Writes the view's fields in place: no allocation.
    private void AnimateGems(HudView v)
    {
        double now = _seconds();
        float delta = _gemsStarted ? GemAnimation.ClampDelta((float)(now - _lastSeconds)) : 0f;
        _lastSeconds = now;
        HudStatusSnapshot st = v.Status;
        float health = st.MaxHealth <= 0 ? 0f : Math.Clamp(st.CurrentHealth / (float)st.MaxHealth, 0f, 1f);
        if (_gemsStarted)
        {
            v.HealthLevel = GemAnimation.Ease(v.HealthLevel, health, delta);
            v.ManaLevel = GemAnimation.Ease(v.ManaLevel, StatusBarPanel.ManaGemFill, delta);
        }
        else
        {
            _gemsStarted = true;
            v.HealthLevel = health;
            v.ManaLevel = Math.Clamp(StatusBarPanel.ManaGemFill, 0f, 1f);
        }

        _animationSeconds = (_animationSeconds + delta) % GemAnimation.LoopSeconds;
        v.AnimationSeconds = (float)_animationSeconds;
        v.GemPulse = GemAnimation.Pulse(v.HealthLevel, v.AnimationSeconds);
    }

    // What the panels picked this frame, cleared by the read.
    public void TakeRequests(out InventoryHudAction? action, out bool closeInventory)
    {
        action = _action;
        closeInventory = _closeInventory;
        _action = null;
        _closeInventory = false;
    }

    void IHudActions.Inventory(InventoryHudAction action) => _action = action;

    void IHudActions.CloseInventory() => _closeInventory = true;

    public void Dispose() => _backend.Dispose();
}
