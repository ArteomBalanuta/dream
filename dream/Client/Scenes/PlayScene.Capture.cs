using System.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Client.Input;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;

namespace Dream.Client.Scenes;

// DREAM_HUD_CAPTURE=<dir> DREAM_HUD_CAPTURE_SCRIPT=play,inventory,admin: after a warm-up, each step
// sets the HUD state, waits a few frames, reads the back buffer before the swap and writes
// <dir>/NN-<step>.png; then the window is asked to close. A verification tool for the real GL
// path. Off unless the directory variable is set; it changes no gameplay and sends nothing itself.
//
// Pointer steps feed synthetic UiPointer states through the real UiInputRouter and panels (a
// scripted pointer replaces the mouse only while a script runs; real input is untouched otherwise),
// so what they do is what a hand on the mouse would do, including the intents that reach the server:
//   hover:<slot>              rest on a slot (tooltip)
//   drag:<from>:<to>          press, carry, drop, then wait for the server's result
//   dragging:<from>:<to>      press and carry, button still down (ghost, drop highlight)
//   burst:<a>:<b>:<n>         n quick drops back and forth, to meet the server's action budget
//   release                   let go where the pointer is, then wait for the result
//   rightclick:<slot>         open the item menu
//   menu:<n>                  click row n of the open menu
//   confirm:destroy|cancel    answer the destroy confirm
//   close                     click the inventory Close button
//   esc, v                    the Esc and V keys' own handlers
//   size:<w>:<h>              resize the window (the same path as the Settings resolution)
//   walkto:<x>:<z>:<maxFrames> click the ground at that cell (projected through last frame's camera, or
//                             the screen edge toward it) until the character is within 1.5 m or the frames run out
//   screenclick:<x>:<y>      left click at a window pixel (for the world or HUD click gate)
//   buildat:<x>:<z>          right click the ground at that cell
//   cell:<x>:<z>             print which levels of that column hold a cell, and the player and goal
//   wait:<frames>, snap, log  idle, capture, print the inventory (and the player's position)
//   fight:<frames>            for that many frames, face the nearest enemy, close in and attack in combat
//                             mode (the real command path), then capture; for damage numbers and death
// <slot> is 0..63 for a bag anchor (row * 8 + col; the drag target is where the item's anchor lands, with the
// item centred on the pointer), or 100..111 / main|off|head|body|hands|feet|legs|neck|ring1|ring2|back|pet
// for an equipment well.
internal sealed class HudCaptureScript
{
    public const string DirectoryVariable = "DREAM_HUD_CAPTURE";
    public const string ScriptVariable = "DREAM_HUD_CAPTURE_SCRIPT";
    public const string SettleVariable = "DREAM_HUD_CAPTURE_SETTLE";
    public const string StepPlay = "play";
    public const string StepInventory = "inventory";
    public const string StepSettings = "settings";
    public const string StepAdmin = "admin";

    // Frames before the first step: atlas decode, model loads and the first snapshots.
    public const int WarmupFrames = 300;

    // Frames between setting a state and reading it.
    public const int SettleFrames = 20;

    public const int MaxSteps = 80;

    private HudCaptureScript(string directory, string[] steps, HudCaptureStepSpec[] specs)
    {
        Directory = directory;
        Steps = steps;
        Specs = specs;
    }

    public string Directory { get; }

    public IReadOnlyList<string> Steps { get; }

    internal IReadOnlyList<HudCaptureStepSpec> Specs { get; }

    // Null unless the directory is set. Unknown step names are dropped; an empty script is "play".
    public static HudCaptureScript? Parse(string? directory, string? script)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        List<string> steps = [];
        List<HudCaptureStepSpec> specs = [];
        foreach (string raw in (script ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string step = raw.ToLowerInvariant();
            if (steps.Count < MaxSteps && HudCaptureStepSpec.TryParse(step, out HudCaptureStepSpec spec))
            {
                steps.Add(step);
                specs.Add(spec);
            }
        }

        if (steps.Count == 0)
        {
            steps.Add(StepPlay);
            specs.Add(new HudCaptureStepSpec(HudCaptureVerb.Play, 0, 0));
        }

        return new HudCaptureScript(directory.Trim(), steps.ToArray(), specs.ToArray());
    }

    public static HudCaptureScript? FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(DirectoryVariable),
        Environment.GetEnvironmentVariable(ScriptVariable));

    // Frames per step; DREAM_HUD_CAPTURE_SETTLE raises it so the perf log gets lines from each state.
    public static int SettleFromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable(SettleVariable), out int frames) && frames > 0
            ? frames
            : SettleFrames;

    // The step text names the file; ':' is not allowed in a Windows file name.
    public string FilePath(int index) => Path.Combine(Directory, $"{index + 1:00}-{Steps[index].Replace(':', '-')}.png");
}

public partial class PlayScene
{
    private enum CapturePhase : byte
    {
        Begin,
        Pointer,
        WaitResult,
        Idle,
        Settle,
        Walk
    }

    // The wait for the server's answer to a sent inventory action, in frames.
    private const int CaptureResultTimeoutFrames = 240;

    private readonly HudCaptureScript? _hudCapture = HudCaptureScript.FromEnvironment();
    private int _hudCaptureFrames;
    private int _hudCaptureStep;
    private int _hudCaptureSettle;
    private readonly int _hudCaptureSettleFrames = HudCaptureScript.SettleFromEnvironment();
    private bool _hudCaptureDone;
    private CapturePhase _capturePhase = CapturePhase.Begin;
    private readonly List<ScriptedPointerFrame> _capturePointerFrames = new();
    private int _capturePointerIndex;
    private int _captureResultFrames;
    private int _captureIdleFrames;
    private int _captureBaselineApplyCount;
    private bool _captureExpectsResult;
    private bool _captureWritesPng;
    private int _captureBeginWait;
    private ScriptedPointerFrame _captureLast;
    private Action<Vector2i>? _captureApplyClientSize;
    private UiScriptedPointer? _captureScriptedPointer;
    private bool _captureFightActive;
    private bool _captureFightModeSent;
    private int _captureFightLogTick;
    private float _captureFightLastDistance;
    private int _captureFightStuckTicks;
    private bool _captureFightNoclipSent;
    private HudCaptureKeys _captureKeys;
    private bool _captureWaitIdle;
    private int _captureTraceFrames;
    private HudCaptureStepSpec _captureWalk;
    private Vector3 _captureWalkStart;
    private int _captureWalkFrames;
    private int _captureWalkClicks;
    private int _captureWalkRest;
    private float _captureWalkMetres;
    private Vector3 _captureWalkLast;

    private bool CaptureRunning =>
        _hudCapture is not null && !_hudCaptureDone && _hudCaptureFrames + 1 >= HudCaptureScript.WarmupFrames
        && _hudCaptureStep < _hudCapture.Specs.Count;

    // Called every frame before the HUD pass. Past the warm-up it runs the script: it begins the
    // current step and sets this frame's synthetic pointer. Does nothing unless a script runs.
    private void AdvanceHudCaptureScript()
    {
        if (!CaptureRunning)
        {
            return;
        }

        HudCaptureScript script = _hudCapture!;
        UiLayout layout = CaptureLayout();
        if (_captureScriptedPointer is null)
        {
            _captureScriptedPointer = new UiScriptedPointer { Active = true };
            _render.ScriptedPointer = _captureScriptedPointer;
            _captureLast = new ScriptedPointerFrame(HudCapturePointerPlan.Parked(in layout), false, false);
            Console.WriteLine("[hud-capture] scripted pointer on (test-only; the mouse is ignored while the script runs)");
            LogHudCaptureInventory("start");
        }

        if (_capturePhase == CapturePhase.Begin)
        {
            // A rejection from the step before is still showing; the next one must be a new one.
            if (!string.IsNullOrEmpty(_actionFeedback.CurrentRejectionMessage) && ++_captureBeginWait < 400)
            {
                SetCapturePointer(_captureLast with { LeftDown = false, RightDown = false });
                return;
            }

            _captureBeginWait = 0;
            BeginHudCaptureStep(script, script.Specs[_hudCaptureStep], in layout);
        }

        if (_capturePhase == CapturePhase.Walk)
        {
            AdvanceCaptureWalk(in layout);
            return;
        }

        if (_capturePhase == CapturePhase.Pointer && _capturePointerIndex < _capturePointerFrames.Count)
        {
            if (_captureTraceFrames > 0 && _capturePointerIndex % _captureTraceFrames == 0)
            {
                LogCaptureStatus("hold frame " + _capturePointerIndex);
            }

            SetCapturePointer(_capturePointerFrames[_capturePointerIndex++]);
            return;
        }

        // Any other phase keeps the pointer where it was, buttons as they were (a held drag stays held).
        SetCapturePointer(_captureLast);
    }

    private void SetCapturePointer(ScriptedPointerFrame frame)
    {
        UiScriptedPointer pointer = _captureScriptedPointer!;
        pointer.Position = frame.Position;
        pointer.LeftDown = frame.LeftDown;
        pointer.RightDown = frame.RightDown;
        _captureLast = frame;
    }

    private UiLayout CaptureLayout()
    {
        Vector2i size = _render.ViewportSize;
        return UiLayout.Create(size.X, size.Y, _settings.UiScaleSetting);
    }

    private void BeginHudCaptureStep(HudCaptureScript script, HudCaptureStepSpec spec, in UiLayout layout)
    {
        string name = script.Steps[_hudCaptureStep];
        _captureBaselineApplyCount = _inventoryApplyCount;
        _captureExpectsResult = false;
        _captureWritesPng = true;
        _capturePointerIndex = 0;
        _capturePointerFrames.Clear();
        _capturePhase = CapturePhase.Settle;
        _hudCaptureSettle = 0;
        InventoryUiState ui = InventoryUiState.Current;

        switch (spec.Verb)
        {
            case HudCaptureVerb.Hover:
            case HudCaptureVerb.RightClick:
            case HudCaptureVerb.Drag:
            case HudCaptureVerb.Dragging:
            case HudCaptureVerb.Burst:
            case HudCaptureVerb.Release:
            case HudCaptureVerb.Close:
            case HudCaptureVerb.Menu:
            case HudCaptureVerb.Confirm:
            {
                UiRect? menuRow = spec.Verb == HudCaptureVerb.Menu && spec.A < ui.MenuRowRects.Count ? ui.MenuRowRects[spec.A] : null;
                UiRect? confirm = spec.Verb == HudCaptureVerb.Confirm && ui.ConfirmOpen
                    ? (spec.A == HudCaptureStepSpec.ConfirmDestroy ? ui.ConfirmDestroyRect : ui.ConfirmCancelRect)
                    : null;
                if (!_settings.InventoryVisible)
                {
                    Console.WriteLine($"[hud-capture] step {name}: the inventory is not open, nothing to aim at");
                    break;
                }

                if (!HudCapturePointerPlan.TryBuild(spec, in layout, _captureLast.Position, menuRow, confirm, _capturePointerFrames, _render.ClassicSurfaceForCapture?.View))
                {
                    Console.WriteLine($"[hud-capture] step {name}: nothing to aim at (no such slot, menu row or dialog)");
                    break;
                }

                _capturePhase = CapturePhase.Pointer;
                // A drop, a menu pick, a confirm answer and a release end in a sent action; the right
                // click, the hover and the drag still holding do not.
                _captureExpectsResult = spec.Verb is HudCaptureVerb.Drag or HudCaptureVerb.Burst or HudCaptureVerb.Release or HudCaptureVerb.Menu or HudCaptureVerb.Confirm;
                Console.WriteLine($"[hud-capture] step {name}: {_capturePointerFrames.Count} pointer frames");
                if (_captureExpectsResult)
                {
                    LogHudCaptureInventory("before " + name);
                }

                break;
            }

            case HudCaptureVerb.Fight:
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = spec.A;
                _captureFightActive = true;
                break;
            case HudCaptureVerb.WalkTo:
                _capturePhase = CapturePhase.Walk;
                _captureWalk = spec;
                _captureWalkStart = _runtime.Player.GetWorldPosition();
                _captureWalkFrames = 0;
                _captureWalkClicks = 0;
                _captureWalkRest = 0;
                _captureWalkMetres = 0f;
                _captureWalkLast = _captureWalkStart;
                _captureWritesPng = false;
                break;
            case HudCaptureVerb.ScreenClick:
                HudCapturePointerPlan.Click(_capturePointerFrames, new Vector2(spec.A, spec.B));
                _capturePhase = CapturePhase.Pointer;
                _captureWritesPng = false;
                break;
            case HudCaptureVerb.BuildAt:
            {
                Vector3 player = _runtime.Player.GetWorldPosition();
                if (TryCaptureWalkClickPoint(in layout, player, new Vector3(spec.A + 0.5f, player.Y, spec.B + 0.5f), out Vector2 at))
                {
                    HudCapturePointerPlan.RightClick(_capturePointerFrames, at);
                    _capturePhase = CapturePhase.Pointer;
                }

                Console.WriteLine($"[hud-capture] buildat {spec.A},{spec.B}: pixel {at.X:0},{at.Y:0}, mode {_runtime.InteractionMode}");
                _captureWritesPng = false;
                break;
            }

            case HudCaptureVerb.EnemyClick:
            {
                EnemyActor? target = NearestLiveEnemy(_runtime.Player.Camera.Position);
                Camera? camera = _render.LastWorldCamera;
                Vector2 enemyAt = default;
                if (target is not null && camera is not null
                    && TryProjectToScreen(camera.GetViewMatrix() * _render.ProjectionMatrix, target.Combatant.GetCombatAnchorWorldPosition(), _render.ViewportSize, out enemyAt, out bool enemyFront)
                    && enemyFront)
                {
                    if (spec.A == 0)
                    {
                        HudCapturePointerPlan.Click(_capturePointerFrames, enemyAt);
                    }
                    else
                    {
                        HudCapturePointerPlan.RightClick(_capturePointerFrames, enemyAt);
                    }

                    _capturePhase = CapturePhase.Pointer;
                }

                Console.WriteLine($"[hud-capture] enemyclick {(spec.A == 0 ? "left" : "right")}: {(target?.DisplayName ?? "no enemy")} pixel {enemyAt.X:0},{enemyAt.Y:0}, clicking={_capturePointerFrames.Count > 0}");
                LogCaptureStatus("enemyclick");
                _captureWritesPng = false;
                break;
            }

            case HudCaptureVerb.GroundClick:
            {
                Vector3 player = _runtime.Player.GetWorldPosition();
                if (TryCaptureWalkClickPoint(in layout, player, new Vector3(spec.A + 0.5f, player.Y, spec.B + 0.5f), out Vector2 groundAt))
                {
                    HudCapturePointerPlan.Click(_capturePointerFrames, groundAt);
                    _capturePhase = CapturePhase.Pointer;
                }

                Console.WriteLine($"[hud-capture] groundclick {spec.A},{spec.B}: pixel {groundAt.X:0},{groundAt.Y:0}, clicking={_capturePointerFrames.Count > 0}");
                _captureWritesPng = false;
                break;
            }

            case HudCaptureVerb.HoldMove:
            {
                Vector3 player = _runtime.Player.GetWorldPosition();
                Vector3 from = new(spec.A & 0xFFFF, player.Y, spec.A >> 16);
                Vector3 to = new(spec.B & 0xFFFF, player.Y, spec.B >> 16);
                Vector2 holdFrom = default;
                Vector2 holdTo = default;
                if (TryCaptureWalkClickPoint(in layout, player, from + new Vector3(0.5f, 0f, 0.5f), out holdFrom)
                    && TryCaptureWalkClickPoint(in layout, player, to + new Vector3(0.5f, 0f, 0.5f), out holdTo))
                {
                    HudCapturePointerPlan.HoldAndMove(_capturePointerFrames, holdFrom, holdTo, spec.C);
                    _capturePhase = CapturePhase.Pointer;
                    _captureTraceFrames = 20;
                }

                Console.WriteLine($"[hud-capture] holdmove {from.X:0},{from.Z:0} -> {to.X:0},{to.Z:0}: pixels {holdFrom.X:0},{holdFrom.Y:0} -> {holdTo.X:0},{holdTo.Y:0}, frames={_capturePointerFrames.Count}");
                _captureWritesPng = false;
                break;
            }

            case HudCaptureVerb.Keys:
                _captureKeys = (HudCaptureKeys)spec.A;
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = spec.B;
                _captureWritesPng = false;
                Console.WriteLine($"[hud-capture] keys {_captureKeys} for {spec.B} frames");
                LogCaptureStatus("keys begin");
                break;
            case HudCaptureVerb.Status:
                LogCaptureStatus("status");
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = 1;
                _captureWritesPng = false;
                break;
            case HudCaptureVerb.WaitIdle:
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = spec.A;
                _captureWaitIdle = true;
                _captureWritesPng = false;
                break;
            case HudCaptureVerb.CellProbe:
                LogCaptureCell(spec.A, spec.B);
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = 1;
                _captureWritesPng = false;
                break;
            case HudCaptureVerb.Wait:
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = spec.A;
                _captureWritesPng = false;
                break;
            case HudCaptureVerb.Log:
                LogHudCaptureInventory("log");
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = 1;
                _captureWritesPng = false;
                break;
            case HudCaptureVerb.Escape:
                PressEscape();
                break;
            case HudCaptureVerb.Key:
                PressInventoryKey();
                break;
            case HudCaptureVerb.Size:
            {
                Vector2i size = new(spec.A, spec.B);
                _captureApplyClientSize?.Invoke(size);
                _settings.ApplyResolution(size);
                Console.WriteLine($"[hud-capture] step {name}: window {size.X}x{size.Y}, ui scale {CaptureLayout().Scale}");
                break;
            }

            case HudCaptureVerb.Snap:
                break;
            default:
                ApplyHudCaptureState(name);
                break;
        }
    }

    // Called at the end of Render, before the swap.
    private void CaptureHudFrameIfRequested()
    {
        HudCaptureScript? script = _hudCapture;
        if (script is null || _hudCaptureDone)
        {
            return;
        }

        if (++_hudCaptureFrames < HudCaptureScript.WarmupFrames)
        {
            return;
        }

        if (_hudCaptureStep >= script.Steps.Count)
        {
            _hudCaptureDone = true;
            _render.ScriptedPointer = null;
            LogHudCaptureInventory("end");
            Console.WriteLine("[hud-capture] done");
            unsafe
            {
                GLFW.SetWindowShouldClose(GLFW.GetCurrentContext(), true);
            }

            return;
        }

        switch (_capturePhase)
        {
            case CapturePhase.Begin:
                return;
            case CapturePhase.Pointer:
                if (_capturePointerIndex >= _capturePointerFrames.Count)
                {
                    // The last pointer frame has been drawn, and its action sent.
                    _capturePhase = _captureExpectsResult ? CapturePhase.WaitResult : CapturePhase.Settle;
                    _captureResultFrames = 0;
                    _hudCaptureSettle = 0;
                }

                return;
            case CapturePhase.WaitResult:
                bool answered = _inventoryApplyCount != _captureBaselineApplyCount
                    || !string.IsNullOrEmpty(_actionFeedback.CurrentRejectionMessage)
                    || InventoryUiState.Current.ConfirmOpen;
                if (!answered && ++_captureResultFrames < CaptureResultTimeoutFrames)
                {
                    return;
                }

                Console.WriteLine(answered
                    ? $"[hud-capture] server answered after {_captureResultFrames} frames (snapshots applied: {_inventoryApplyCount - _captureBaselineApplyCount}, rejection: {_actionFeedback.CurrentRejectionMessage ?? "none"})"
                    : "[hud-capture] no answer from the server within the timeout");
                _capturePhase = CapturePhase.Settle;
                _hudCaptureSettle = 0;
                return;
            case CapturePhase.Walk:
                return;
            case CapturePhase.Idle:
                if (_captureWaitIdle && _clickMove.Goal == ClickGoalKind.None)
                {
                    break;
                }

                if (_captureWaitIdle && _captureIdleFrames % 60 == 0)
                {
                    LogCaptureStatus("waiting");
                }

                if (--_captureIdleFrames > 0)
                {
                    return;
                }

                break;
            default:
                if (++_hudCaptureSettle <= _hudCaptureSettleFrames)
                {
                    return;
                }

                break;
        }

        string step = script.Steps[_hudCaptureStep];
        if (_captureWritesPng)
        {
            string path = script.FilePath(_hudCaptureStep);
            try
            {
                _render.CaptureBackBufferPng(path);
                Console.WriteLine($"[hud-capture] wrote {path} surface={_render.CurrentHudSurface(_settings)} quads={_render.ClassicHudQuadCount} commands={_render.ClassicHudCommandCount} dropped={_render.ClassicHudDroppedQuads}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.WriteLine($"[hud-capture] could not write {path}: {ex.Message}");
            }
        }

        if (_captureExpectsResult)
        {
            LogHudCaptureInventory("after " + step);
        }

        if (_captureKeys != HudCaptureKeys.None || _captureWaitIdle)
        {
            LogCaptureStatus(_captureWaitIdle ? "waitidle end" : "keys end");
        }

        _hudCaptureStep++;
        _hudCaptureSettle = 0;
        _capturePhase = CapturePhase.Begin;
        _captureFightActive = false;
        _captureKeys = HudCaptureKeys.None;
        _captureWaitIdle = false;
        _captureTraceFrames = 0;
    }

    private bool CaptureKeyHeld(Keys key) => _captureKeys != HudCaptureKeys.None && key switch
    {
        Keys.W => _captureKeys.HasFlag(HudCaptureKeys.W),
        Keys.A => _captureKeys.HasFlag(HudCaptureKeys.A),
        Keys.S => _captureKeys.HasFlag(HudCaptureKeys.S),
        Keys.D => _captureKeys.HasFlag(HudCaptureKeys.D),
        Keys.Space => _captureKeys.HasFlag(HudCaptureKeys.Space),
        Keys.LeftControl => _captureKeys.HasFlag(HudCaptureKeys.Ctrl),
        Keys.D0 => _captureKeys.HasFlag(HudCaptureKeys.Noclip),
        Keys.D1 => _captureKeys.HasFlag(HudCaptureKeys.Building),
        Keys.D2 => _captureKeys.HasFlag(HudCaptureKeys.Combat),
        Keys.Q => _captureKeys.HasFlag(HudCaptureKeys.Q),
        Keys.E => _captureKeys.HasFlag(HudCaptureKeys.E),
        _ => false
    };

    private EnemyActor? NearestLiveEnemy(Vector3 from)
    {
        EnemyActor? nearest = null;
        float best = float.MaxValue;
        foreach (EnemyActor enemy in _runtime.Enemies.ActiveActors)
        {
            float d = (enemy.Position - from).LengthSquared;
            if (enemy.IsActive && d < best)
            {
                best = d;
                nearest = enemy;
            }
        }

        return nearest;
    }

    // One line of what a click proof needs: where the player is, what the controller holds, the mode,
    // and the nearest live enemy.
    private void LogCaptureStatus(string tag)
    {
        Vector3 at = _runtime.Player.GetWorldPosition();
        EnemyActor? enemy = NearestLiveEnemy(_runtime.Player.Camera.Position);
        string near = enemy is null
            ? "none"
            : $"{enemy.DisplayName} at {enemy.Position.X:0.0},{enemy.Position.Z:0.0} {(enemy.Position - _runtime.Player.Camera.Position).Length:0.0} m hp {enemy.Combatant.Health.CurrentHealth}";
        StringBuilder all = new();
        foreach (EnemyActor each in _runtime.Enemies.ActiveActors)
        {
            all.Append(" #").Append(each.Combatant.Actor.Id).Append('@').Append(each.Position.X.ToString("0.0")).Append(',').Append(each.Position.Z.ToString("0.0"))
                .Append(" hp").Append(each.Combatant.Health.CurrentHealth).Append(each.IsActive ? string.Empty : "(dead)");
        }

        Console.WriteLine($"[hud-capture] enemies:{all}");
        Console.WriteLine(
            $"[hud-capture] status {tag}: pos {at.X:0.0},{at.Y:0.0},{at.Z:0.0} goal {_clickMove.Goal} cell {_clickMove.GoalCell.X},{_clickMove.GoalCell.Level},{_clickMove.GoalCell.Z} " +
            $"goalCellSolid {_runtime.Map.TryGetGeometryCellAtCell(_clickMove.GoalCell.X, _clickMove.GoalCell.Level, _clickMove.GoalCell.Z) is not null} walking {_clickMove.IsWalking} pathCells {_clickMove.LastPathCells} mode {_runtime.InteractionMode} noclip {_runtime.Player.IsNoclipEnabled} " +
            $"alive {_runtime.Player.Combatant.Health.IsAlive} hp {_runtime.Player.Combatant.Health.CurrentHealth} nearest {near} " +
            $"target {(_clickMove.Target is { } t ? $"alive {t.IsAlive} {(t.Anchor - _runtime.Player.Camera.Position).Length:0.0} m at {t.Anchor.X:0.0},{t.Anchor.Z:0.0}" : "none")}");
    }

    private const float CaptureWalkReachMetres = 1.5f;
    private const int CaptureWalkRestFrames = 4;

    // The walkto step, one call per frame. While nothing is moving the character and no click is
    // pending, it clicks the target through the real pointer path; it ends within 1.5 m or at the cap.
    private void AdvanceCaptureWalk(in UiLayout layout)
    {
        Vector3 player = _runtime.Player.GetWorldPosition();
        Vector3 target = new(_captureWalk.A + 0.5f, player.Y, _captureWalk.B + 0.5f);
        float remaining = new Vector2(player.X - target.X, player.Z - target.Z).Length;
        _captureWalkFrames++;
        _captureWalkMetres += new Vector2(player.X - _captureWalkLast.X, player.Z - _captureWalkLast.Z).Length;
        _captureWalkLast = player;
        if (remaining <= CaptureWalkReachMetres || _captureWalkFrames >= _captureWalk.C)
        {
            bool reached = remaining <= CaptureWalkReachMetres;
            Console.WriteLine(
                $"[hud-capture] walkto {_captureWalk.A},{_captureWalk.B} start={_captureWalkStart.X:0.0},{_captureWalkStart.Y:0.0},{_captureWalkStart.Z:0.0} " +
                $"end={player.X:0.0},{player.Y:0.0},{player.Z:0.0} clicks={_captureWalkClicks} pathCells={_clickMove.LastPathCells} walked={_captureWalkMetres:0.0} reached={(reached ? "true" : "false")}");
            _capturePointerFrames.Clear();
            _capturePointerIndex = 0;
            SetCapturePointer(_captureLast with { LeftDown = false, RightDown = false });
            _capturePhase = CapturePhase.Idle;
            _captureIdleFrames = 1;
            return;
        }

        if (_capturePointerIndex < _capturePointerFrames.Count)
        {
            SetCapturePointer(_capturePointerFrames[_capturePointerIndex++]);
            return;
        }

        SetCapturePointer(_captureLast with { LeftDown = false, RightDown = false });
        bool controllerIdle = _clickMove.Goal == ClickGoalKind.None && !_clickMove.IsWalking;
        // A few frames of rest between clicks, so the previous one has been seen by an Update.
        if (!controllerIdle || ++_captureWalkRest < CaptureWalkRestFrames || _render.LastWorldCamera is null)
        {
            return;
        }

        if (TryCaptureWalkClickPoint(in layout, player, target, out Vector2 at))
        {
            _capturePointerFrames.Clear();
            _capturePointerIndex = 0;
            HudCapturePointerPlan.Click(_capturePointerFrames, at);
            _captureWalkClicks++;
            _captureWalkRest = 0;
        }
    }

    // Where on screen to click for a target on the ground: its projection, or when that is off
    // screen (or under the bar) the point on the line from the player to it, inset 15%.
    private bool TryCaptureWalkClickPoint(in UiLayout layout, Vector3 player, Vector3 target, out Vector2 at)
    {
        at = default;
        Camera? camera = _render.LastWorldCamera;
        if (camera is null)
        {
            return false;
        }

        Matrix4 viewProjection = camera.GetViewMatrix() * _render.ProjectionMatrix;
        Vector2i size = _render.ViewportSize;
        Vector3 feet = new(player.X, player.Y - 0.5f, player.Z);
        Vector3 ground = new(target.X, feet.Y, target.Z);
        if (!TryProjectToScreen(viewProjection, ground, size, out Vector2 targetScreen, out bool targetInFront)
            || !TryProjectToScreen(viewProjection, feet, size, out Vector2 playerScreen, out _))
        {
            return false;
        }

        float left = size.X * 0.15f;
        float right = size.X * 0.85f;
        float top = size.Y * 0.15f;
        float bottom = MathF.Min(size.Y * 0.85f, layout.Bar.Y - 8f);
        if (targetInFront && targetScreen.X >= left && targetScreen.X <= right && targetScreen.Y >= top && targetScreen.Y <= bottom)
        {
            at = targetScreen;
            return true;
        }

        Vector2 d = targetInFront ? targetScreen - playerScreen : playerScreen - targetScreen;
        if (d.LengthSquared < 1e-6f)
        {
            return false;
        }

        float t = float.MaxValue;
        if (d.X > 0f) { t = MathF.Min(t, (right - playerScreen.X) / d.X); }
        else if (d.X < 0f) { t = MathF.Min(t, (left - playerScreen.X) / d.X); }
        if (d.Y > 0f) { t = MathF.Min(t, (bottom - playerScreen.Y) / d.Y); }
        else if (d.Y < 0f) { t = MathF.Min(t, (top - playerScreen.Y) / d.Y); }
        if (t == float.MaxValue || t < 0f)
        {
            return false;
        }

        // Never past the target itself when it is in front.
        if (targetInFront)
        {
            t = MathF.Min(t, 1f);
        }

        at = playerScreen + (d * t);
        return true;
    }

    private static bool TryProjectToScreen(Matrix4 viewProjection, Vector3 world, Vector2i size, out Vector2 screen, out bool inFront)
    {
        Vector4 clip = Vector4.TransformRow(new Vector4(world, 1f), viewProjection);
        inFront = clip.W > 0f;
        if (MathF.Abs(clip.W) < 1e-6f)
        {
            screen = default;
            return false;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        screen = new Vector2((ndcX + 1f) * 0.5f * size.X, (1f - ndcY) * 0.5f * size.Y);
        return true;
    }

    // The fight step's input: through the same command frame a hand produces. Turns toward the
    // nearest live enemy with look deltas (the server integrates those, not the client camera),
    // walks in until close, holds primary use and asks for combat mode once.
    private PlayerCommandFrame ApplyCaptureFight(PlayerCommandFrame frame)
    {
        if (!_captureFightActive)
        {
            return frame;
        }

        Vector3 from = _runtime.Player.Camera.Position;
        EnemyActor? nearest = null;
        float best = float.MaxValue;
        foreach (EnemyActor enemy in _runtime.Enemies.ActiveActors)
        {
            float d = (enemy.Position - from).LengthSquared;
            if (enemy.IsActive && d < best)
            {
                best = d;
                nearest = enemy;
            }
        }

        PlayerInteractionMode? mode = _captureFightModeSent ? null : PlayerInteractionMode.Combat;
        _captureFightModeSent = true;
        if (nearest is null)
        {
            return frame with { RequestedMode = new PlayerModeSwitchIntent(mode) };
        }

        Vector3 toEnemy = (nearest.Position + new Vector3(0f, 0.4f, 0f)) - from;
        float distance = toEnemy.Length;
        Vector3 front = _runtime.Player.Camera.Front;
        float yaw = MathF.Atan2(front.Z, front.X);
        float pitch = MathF.Asin(Math.Clamp(front.Y, -1f, 1f));
        float wantYaw = MathF.Atan2(toEnemy.Z, toEnemy.X);
        float wantPitch = MathF.Asin(Math.Clamp(toEnemy.Y / MathF.Max(distance, 0.001f), -1f, 1f));
        float dYaw = MathHelper.NormalizeRadians(wantYaw - yaw);
        float dPitch = wantPitch - pitch;
        // Camera.ApplyLookDelta: yaw += dx * s / 60, pitch -= dy * s / 60.
        float perPixel = _settings.MouseSensitivity / 60f;
        if (++_captureFightLogTick % 120 == 1)
        {
            Console.WriteLine($"[hud-capture] fight: {nearest.DisplayName} at {distance:0.0} m, off aim yaw {dYaw:0.000} pitch {dPitch:0.000} rad, own pos {from.X:0.0},{from.Z:0.0}, own hp {_runtime.Player.Combatant.Health.CurrentHealth}, enemy hp {nearest.Combatant.Health.CurrentHealth}");
        }

        // Walking into a ledge makes no progress: jump while stuck, and sidestep when that is not enough.
        _captureFightStuckTicks = distance > 1.8f && _captureFightLastDistance - distance < 0.002f
            ? _captureFightStuckTicks + 1
            : 0;
        _captureFightLastDistance = distance;
        bool jump = _captureFightStuckTicks > 20 && (_captureFightStuckTicks % 40) < 6;
        // A wall that jumping does not clear (the enemies' pen): fly through it once with noclip.
        bool noclip = _captureFightStuckTicks > 200 && !_captureFightNoclipSent;
        _captureFightNoclipSent |= noclip;
        float strafe = _captureFightStuckTicks > 150 && !_captureFightNoclipSent ? (((_captureFightStuckTicks / 90) % 2) == 0 ? 1f : -1f) : 0f;

        return frame with
        {
            RequestedMode = new PlayerModeSwitchIntent(mode),
            MoveIntent = new PlayerMoveIntent(distance > 1.8f ? 1f : 0f, strafe, 0f, jump && !_captureFightNoclipSent, false, noclip),
            LookIntent = new PlayerLookIntent(dYaw / perPixel, -dPitch / perPixel),
            PrimaryUsePressed = true
        };
    }

    // The bag, the worn items, the character window's stat rows and the key HUD state: what the client
    // holds, which after a server result is the server's state.
    private void LogHudCaptureInventory(string tag)
    {
        StringBuilder sb = new();
        sb.Append("[hud-capture] inventory ").Append(tag).Append(" (snapshots applied ").Append(_inventoryApplyCount).AppendLine("):");
        if (_inventory is not PlayerInventorySnapshot inv)
        {
            sb.AppendLine("  no snapshot");
        }
        else
        {
            sb.Append("  bag (anchor=item):");
            foreach (InventorySlotSnapshot slot in inv.Slots)
            {
                if (slot.Item is InventoryItemSnapshot item)
                {
                    sb.Append(' ').Append(slot.SlotIndex).Append('=').Append(DescribeItem(item)).Append(slot.IsEquipped ? "*" : string.Empty);
                }
            }

            sb.AppendLine();
            sb.Append("  worn:");
            if (inv.EquippedSlot?.Item is InventoryItemSnapshot main)
            {
                sb.Append(" 100=").Append(DescribeItem(main));
            }

            if (inv.Equipment is { } equipment)
            {
                foreach (InventorySlotSnapshot slot in equipment)
                {
                    if (slot.Item is InventoryItemSnapshot item)
                    {
                        sb.Append(' ').Append(slot.SlotIndex).Append('=').Append(DescribeItem(item));
                    }
                }
            }

            sb.AppendLine();
        }

        HudView? view = _render.ClassicSurfaceForCapture?.View;
        if (view?.StatRows is { Length: > 0 } rows)
        {
            sb.Append("  stats:");
            foreach (CharacterStatRow row in rows)
            {
                sb.Append(' ').Append(row.Label).Append('=').Append(row.EffectiveText);
                if (!string.IsNullOrEmpty(row.BonusText))
                {
                    sb.Append('(').Append(row.BaseText).Append(' ').Append(row.BonusText).Append(')');
                }
            }

            sb.AppendLine();
        }

        Vector3 at = _runtime.Player.GetWorldPosition();
        sb.Append("  player: ").Append(at.X.ToString("0.0")).Append(',').Append(at.Y.ToString("0.0")).Append(',').Append(at.Z.ToString("0.0")).AppendLine();
        sb.Append("  inventory open: ").Append(_settings.InventoryVisible).Append(", settings open: ").Append(_settings.SettingsVisible).Append(", cursor: ").Append(DesiredCursorState);
        Console.WriteLine(sb.ToString());
    }

    private void LogCaptureCell(int x, int z)
    {
        StringBuilder sb = new();
        for (int y = 1; y <= 4; y++)
        {
            sb.Append(_runtime.Map.TryGetGeometryCellAtCell(x, y, z) is null ? " ." : " " + y);
        }

        Vector3 at = _runtime.Player.GetWorldPosition();
        Console.WriteLine($"[hud-capture] cell {x},{z}: levels{sb} player {at.X:0.0},{at.Y:0.0},{at.Z:0.0} goal {_clickMove.Goal}");
    }

    private static string DescribeItem(InventoryItemSnapshot item)
    {
        string mods = item.Modifiers is { Count: > 0 } m
            ? "[" + string.Join(';', m.Select(x => $"{x.Stat} {x.Op} {x.Value:0.##}")) + "]"
            : string.Empty;
        return $"#{item.ItemInstanceId}:{item.ItemKind}:{item.Rarity}{(item.StackCount > 1 ? "x" + item.StackCount : string.Empty)}{mods}";
    }

    private void ApplyHudCaptureState(string step)
    {
        bool wantInventory = step == HudCaptureScript.StepInventory;
        bool wantSettings = step == HudCaptureScript.StepSettings;
        bool wantAdmin = step == HudCaptureScript.StepAdmin;

        if (_settings.SettingsVisible && !wantSettings)
        {
            _settings.ToggleSettings();
        }

        if (_settings.InventoryVisible && !wantInventory)
        {
            _settings.ToggleInventory();
        }

        if (wantInventory && !_settings.InventoryVisible)
        {
            _settings.ToggleInventory();
        }

        if (wantSettings && !_settings.SettingsVisible)
        {
            _settings.ToggleSettings();
        }

        bool imGuiSurface = _render.CurrentHudSurface(_settings) == HudSurface.ImGui;
        if (imGuiSurface != wantAdmin)
        {
            // Same path as F3: a no-op unless the admin setting is available (DREAM_ADMIN_HUD=1).
            _render.ToggleAdminHud(_settings);
        }

        Console.WriteLine($"[hud-capture] step {step}: surface={_render.CurrentHudSurface(_settings)} admin={_settings.AdminHudAvailable}");
    }
}
