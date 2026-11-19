using System.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Presentation;
using Dream.Rendering.Hud;
using Dream.Rendering.Models;
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
//   vendor                    open the bag and interact with the nearest NPC, then idle for the
//                             server's stock; the PNG shows the shop board beside the bag
//   esc, v                    the Esc and V keys' own handlers
//   size:<w>:<h>              resize the window (the same path as the Settings resolution)
//   walkto:<x>:<z>:<maxFrames> click the ground at that cell (projected through last frame's camera, or
//                             the screen edge toward it) until the character is within 1.5 m or the frames run out
//   screenclick:<x>:<y>      left click at a window pixel (for the world or HUD click gate); in Combat the left
//                             button attacks and aims, so over the world this is "attack at this pixel"
//   aim:<x>:<y>              latch the pointer at a window pixel, no button; it stays there through the
//                             steps after (keys, status, snap...), so the cursor aim has a fixed cursor. Pick a
//                             pixel over the world, not over the bar or a window
//   aimenemy                 aim: at the nearest live enemy's pixel when the step begins
//   holdleft:<frames>        left button down for that many frames at the latched pointer, then released
//                             (Combat: the held attack; press on the first frame, repeats at the server's cadence)
//   buildat:<x>:<z>          right click the ground at that cell
//   (walkto, groundclick, holdmove and enemyclick:right use the right button, which is click-to-move;
//    walk goals need Combat mode, so put g in the script first)
//   keys:<names>:<frames>    hold keys through the real key mapping for N frames, then release; no PNG
//   keysnap:<names>:<frames> the same hold, but the PNG is written on the LAST HELD frame, before the
//                             release, so the capture shows a held pose (walk, strafe, back). The status
//                             line after it reports the keys the written frame was rendered with
//   status                   the player, the click goal, the mode, the enemies, and actorbox <x0>,<y0>,<x1>,<y1>:
//                             the local player's projected screen bbox, clamped to the viewport and the bar,
//                             or "actorbox none: <reason>" when there is no figure to measure; and
//                             actorclip <clip>, the animation clip the state machine selected for that
//                             frame with its raw delta and residual (acceptance M6)
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

    // Frames to idle after a vendor interact, for the server's VendorOpenedResult to arrive.
    private const int CaptureVendorWaitFrames = 60;

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
    // Where the pointer rests between steps: the last frame set, so aim: and every step after it keep one cursor.
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
    private bool _captureKeySnap;
    private bool _captureWaitIdle;
    private System.Diagnostics.Stopwatch? _captureHoldLeftWatch;
    private readonly Dictionary<int, int> _captureHitHealth = new();
    private readonly HashSet<int> _captureHitSeen = new();
    private readonly List<int> _captureHitDead = new();
    private int _captureHitCount;
    private int _captureHitFrame;
    private bool _captureHoldLeftEnded;
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

        TrackCaptureHits();
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

    // During holdleft: the combat log this build lacks. Prints every drop of an enemy's health with the
    // frame and the milliseconds since the press, so the first hit, the cadence and any hit on release show.
    private void TrackCaptureHits()
    {
        if (_captureHoldLeftWatch is not { } watch)
        {
            return;
        }

        _captureHitFrame++;
        string when = _captureHoldLeftEnded ? " AFTER THE HOLD STEP ENDED" : string.Empty;
        _captureHitSeen.Clear();
        foreach (EnemyActor enemy in _runtime.Enemies.ActiveActors)
        {
            int id = enemy.Combatant.Actor.Id;
            int hp = enemy.IsActive ? enemy.Combatant.Health.CurrentHealth : 0;
            _captureHitSeen.Add(id);
            if (_captureHitHealth.TryGetValue(id, out int before) && hp < before)
            {
                _captureHitCount++;
                Console.WriteLine($"[hud-capture] hit #{_captureHitCount}: enemy #{id} hp {before} -> {hp} at frame {_captureHitFrame} ({watch.Elapsed.TotalMilliseconds:0} ms){when}");
            }

            _captureHitHealth[id] = hp;
        }

        // A killed enemy leaves the active list: its last hit is the one that took it from hp to gone.
        foreach (KeyValuePair<int, int> tracked in _captureHitHealth)
        {
            if (tracked.Value > 0 && !_captureHitSeen.Contains(tracked.Key))
            {
                _captureHitCount++;
                Console.WriteLine($"[hud-capture] hit #{_captureHitCount}: enemy #{tracked.Key} hp {tracked.Value} -> gone (killed) at frame {_captureHitFrame} ({watch.Elapsed.TotalMilliseconds:0} ms){when}");
                _captureHitDead.Add(tracked.Key);
            }
        }

        foreach (int dead in _captureHitDead)
        {
            _captureHitHealth[dead] = 0;
        }

        _captureHitDead.Clear();
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
                // The step steers with look deltas; the per-tick cursor aim would overwrite them.
                SuppressCursorAim = true;
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
            case HudCaptureVerb.Aim:
                HudCapturePointerPlan.Aim(_capturePointerFrames, new Vector2(spec.A, spec.B));
                _capturePhase = CapturePhase.Pointer;
                _captureWritesPng = false;
                Console.WriteLine($"[hud-capture] aim {spec.A},{spec.B}: pointer latched, no button, over hud {ClassicHudHitTest.Over(in layout, _settings.InventoryVisible, new Vector2(spec.A, spec.B))}");
                break;
            case HudCaptureVerb.AimEnemy:
            {
                EnemyActor? aimTarget = NearestLiveEnemy(_runtime.Player.Camera.Position);
                Camera? aimCamera = _render.LastWorldCamera;
                Vector2 aimAt = default;
                if (aimTarget is not null && aimCamera is not null
                    && TryProjectToScreen(aimCamera.GetViewMatrix() * _render.ProjectionMatrix, aimTarget.Combatant.GetCombatAnchorWorldPosition(), _render.ViewportSize, out aimAt, out bool aimFront)
                    && aimFront)
                {
                    HudCapturePointerPlan.Aim(_capturePointerFrames, aimAt);
                    _capturePhase = CapturePhase.Pointer;
                }

                Console.WriteLine($"[hud-capture] aimenemy: {(aimTarget?.DisplayName ?? "no enemy")} pixel {aimAt.X:0},{aimAt.Y:0}, latched={_capturePointerFrames.Count > 0}");
                _captureWritesPng = false;
                break;
            }

            case HudCaptureVerb.HoldLeft:
                HudCapturePointerPlan.HoldLeft(_capturePointerFrames, _captureLast.Position, spec.A);
                _capturePhase = CapturePhase.Pointer;
                _captureWritesPng = false;
                _captureHoldLeftWatch = System.Diagnostics.Stopwatch.StartNew();
                _captureHitHealth.Clear();
                _captureHitCount = 0;
                _captureHitFrame = 0;
                _captureHoldLeftEnded = false;
                Console.WriteLine($"[hud-capture] holdleft {spec.A} frames at {_captureLast.Position.X:0},{_captureLast.Position.Y:0}, mode {_runtime.InteractionMode}, attack interval {_runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds:0.000} s (client-side stats)");
                LogCaptureStatus("holdleft begin");
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
                    HudCapturePointerPlan.RightClick(_capturePointerFrames, groundAt);
                    _capturePhase = CapturePhase.Pointer;
                }

                Console.WriteLine($"[hud-capture] groundclick {spec.A},{spec.B} (right button): pixel {groundAt.X:0},{groundAt.Y:0}, clicking={_capturePointerFrames.Count > 0}");
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
                    HudCapturePointerPlan.HoldAndMove(_capturePointerFrames, holdFrom, holdTo, spec.C, right: true);
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
            case HudCaptureVerb.KeySnap:
                // keys:, except that the PNG is written: the idle countdown ends on the last held frame
                // and the write below happens before the keys are released at the end of the step, so
                // the frame on disk is a frame rendered with the keys down (acceptance M7).
                _captureKeys = (HudCaptureKeys)spec.A;
                _captureKeySnap = true;
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = spec.B;
                Console.WriteLine($"[hud-capture] keysnap {_captureKeys} for {spec.B} frames, the png on the last held frame");
                LogCaptureStatus("keysnap begin");
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
            case HudCaptureVerb.Vendor:
            {
                // The real path: the vendor window only stays open while the bag is open (see
                // UpdateEconomyState), and the stock arrives from the server, so open the bag,
                // press interact and idle until VendorOpenedResult lands.
                if (!_settings.InventoryVisible)
                {
                    PressInventoryKey();
                }

                bool reached = TryInteractWithNearest();
                Console.WriteLine($"[hud-capture] vendor: interact sent={reached}, bag open={_settings.InventoryVisible}");
                _capturePhase = CapturePhase.Idle;
                _captureIdleFrames = CaptureVendorWaitFrames;
                break;
            }

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
            // Logged on the frame being written, immediately above its "wrote" line, so a crop can be
            // tied to one PNG without guessing which `status` step it belonged to.
            LogCaptureActorBox();
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

        if (_captureHoldLeftWatch is { } watch && !_captureHoldLeftEnded)
        {
            // The watch keeps running through the steps after, so a hit that lands after the release shows up tagged.
            _captureHoldLeftEnded = true;
            Console.WriteLine($"[hud-capture] holdleft end: {watch.Elapsed.TotalSeconds:0.00} s wall clock, {_captureHitFrame} frames, {_captureHitCount} hits so far");
            LogCaptureStatus("holdleft end");
        }

        if (_captureKeys != HudCaptureKeys.None || _captureWaitIdle)
        {
            // For keysnap this line is the proof the verb exists for: it prints after the write above and
            // before the release below, so the keys and the axes it reports are the ones the written
            // frame was rendered with.
            LogCaptureStatus(_captureWaitIdle ? "waitidle end" : _captureKeySnap ? "keysnap wrote the png, keys still held" : "keys end");
        }

        _hudCaptureStep++;
        _hudCaptureSettle = 0;
        _capturePhase = CapturePhase.Begin;
        _captureFightActive = false;
        SuppressCursorAim = false;
        _captureKeys = HudCaptureKeys.None;
        _captureKeySnap = false;
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
        Keys.B => _captureKeys.HasFlag(HudCaptureKeys.Building),
        Keys.G => _captureKeys.HasFlag(HudCaptureKeys.Combat),
        Keys.Q => _captureKeys.HasFlag(HudCaptureKeys.Q),
        Keys.R => _captureKeys.HasFlag(HudCaptureKeys.R),
        Keys.LeftShift => _captureKeys.HasFlag(HudCaptureKeys.Shift),
        Keys.Z => _captureKeys.HasFlag(HudCaptureKeys.Z),
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
        // Where the character stands, which way it faces (Camera.Front, the facing the server moves it
        // with) and the forward/strafe axes the held keys resolve to against that facing, on one line.
        Vector3 front = _runtime.Player.Camera.Front;
        (float forwardAxis, float strafeAxis) = _tickCameraAxes == Vector2.Zero
            ? (0f, 0f)
            : PlayerController.Project(_tickCameraAxes, front);
        Console.WriteLine(
            $"[hud-capture] status {tag}: pos {at.X:0.00},{at.Y:0.00},{at.Z:0.00} facing {front.X:0.00},{front.Z:0.00} (yaw {MathHelper.RadiansToDegrees(MathF.Atan2(front.Z, front.X)):0}) " +
            $"axes fwd {forwardAxis:0.00} strafe {strafeAxis:0.00} keys {_captureKeys} pointer {_captureLast.Position.X:0},{_captureLast.Position.Y:0} left {_captureLast.LeftDown} aimOwnsFacing {_cursorAim.OwnsFacing}");
        Console.WriteLine(
            $"[hud-capture] status {tag}: goal {_clickMove.Goal} cell {_clickMove.GoalCell.X},{_clickMove.GoalCell.Level},{_clickMove.GoalCell.Z} " +
            $"goalCellSolid {_runtime.Map.TryGetGeometryCellAtCell(_clickMove.GoalCell.X, _clickMove.GoalCell.Level, _clickMove.GoalCell.Z) is not null} walking {_clickMove.IsWalking} pathCells {_clickMove.LastPathCells} mode {_runtime.InteractionMode} noclip {_runtime.Player.IsNoclipEnabled} " +
            $"alive {_runtime.Player.Combatant.Health.IsAlive} hp {_runtime.Player.Combatant.Health.CurrentHealth} nearest {near} " +
            $"target {(_clickMove.Target is { } t ? $"alive {t.IsAlive} {(t.Anchor - _runtime.Player.Camera.Position).Length:0.0} m at {t.Anchor.X:0.0},{t.Anchor.Z:0.0}" : "none")}");
        LogCaptureActorBox();
        LogCaptureActorClip();
    }

    // The clip the player's state machine actually selected this frame (acceptance M6). Logged next
    // to actorbox so a capture carries the clip NAME instead of leaving it to be derived from the
    // axes plus a reading of ResolveWalkClip - a derivation that cannot catch a state machine which
    // computes the right sector and then plays the wrong clip. The raw delta and the residual are on
    // the same line, which is exactly contract C2's table: clip, raw |delta|, residual.
    private void LogCaptureActorClip()
    {
        if (_render.LocalPlayers.LastAnimationFrame is not { } frame)
        {
            Console.WriteLine(
                "[hud-capture] actorclip none: the local player has not been drawn " +
                "(first person, or the player model is not uploaded yet)");
            return;
        }

        float raw = _render.LocalPlayers.LastRawLegTwistRadians;
        Console.WriteLine(
            $"[hud-capture] actorclip {frame.Clip} mode {frame.Mode} playbackSpeed {frame.PlaybackSpeed:0.00} " +
            $"rawDeltaDeg {MathHelper.RadiansToDegrees(raw):0.0} residualDeg {MathHelper.RadiansToDegrees(frame.LegTwistRadians):0.0} " +
            $"airTuck {frame.AirTuck:0.00} hitFlinch {frame.HitFlinch:0.00} dead {frame.IsDead}");
    }

    // The local player's projected screen bbox, so an actor crop is the renderer's own number instead of
    // a hand-typed guess at where the figure is (acceptance M7; check_look.py --actor-from-log reads it).
    //
    // The box is the UNION of two things, so it is never smaller than the figure drawn:
    //  1. the player model's authored bounds under the same model matrix the renderer builds
    //     (PlayerRenderState.Create): the authored extent scaled by VisualScale, the feet at the body
    //     centre plus BodyCenterToFootOffset, rotated by the yaw the facing resolves to; and
    //  2. the eight corners of every box of every worn and held piece, exactly as the renderers place
    //     them last draw (LocalPlayerRenderer.TryAppendDrawnEquipmentCorners, from the real skeleton
    //     pose, so a swung leg's boot and a raised blade are in it). Armour is separate geometry
    //     attached at render time and stands outside the authored bounds (a hooded cloak panel at
    //     z -0.385, a grave mantle at x +-0.41), so the authored box alone clips exactly the pieces A3
    //     measures. An unequipped actor adds nothing and keeps the authored box.
    // Remaining limit, stated rather than hidden: the authored extent is centred on the position, so an
    // off-centre arm can fall up to ~5 cm outside it, and the pose layers (a swung limb of the authored
    // mesh itself) are not in it. Boxes are projected corner by corner, so they are the AABB of the
    // drawn boxes, not of the rendered pixels.
    //
    // When the figure is not there to be measured the line says so and gives no numbers. A missing box is
    // a loud failure; a clamped-to-nothing box would be a silently wrong measurement.
    private void LogCaptureActorBox()
    {
        if (_settings.CameraViewMode == CameraViewMode.FirstPerson)
        {
            Console.WriteLine("[hud-capture] actorbox none: first person, the local player is not drawn");
            return;
        }

        Camera? camera = _render.LastWorldCamera;
        if (camera is null)
        {
            Console.WriteLine("[hud-capture] actorbox none: no world camera has been drawn yet");
            return;
        }

        WorldAssetDimensions size = PlayerModelAssetDefinition.Normalization.ResolveScaledDimensions();
        if (size.Height <= 0f)
        {
            Console.WriteLine("[hud-capture] actorbox none: the player model's authored bounds are not available");
            return;
        }

        LocalPlayerRenderPose pose = _runtime.LocalRenderPose;
        Vector3 facing = pose.Facing.LengthSquared > 0.0001f ? pose.Facing.Normalized() : PlayerModelAssetDefinition.AuthoredForwardAxis;
        float yaw = MathF.Atan2(facing.X, facing.Z) - MathF.Atan2(PlayerModelAssetDefinition.AuthoredForwardAxis.X, PlayerModelAssetDefinition.AuthoredForwardAxis.Z);
        Vector3 feet = pose.Position + PlayerModelAssetDefinition.BodyCenterToFootOffset;

        List<Vector3> equipment = new(64);
        Vector3 drawnFeet = feet;
        if (_render.LocalPlayers.TryAppendDrawnEquipmentCorners(equipment, out Matrix4 drawnModel))
        {
            drawnFeet = drawnModel.Row3.Xyz;
        }

        List<Vector3> points = BuildActorPoints(feet, yaw, size, equipment, drawnFeet);
        int equipmentCorners = equipment.Count;

        Matrix4 viewProjection = camera.GetViewMatrix() * _render.ProjectionMatrix;
        Vector2i viewport = _render.ViewportSize;
        ProjectBounds(points, viewProjection, viewport, out float x0, out float y0, out float x1, out float y1, out int behind);

        if (behind == points.Count)
        {
            Console.WriteLine("[hud-capture] actorbox none: the player is behind the camera");
            return;
        }

        // The bar is opaque and drawn over the world, so anything under its top edge is not the figure.
        UiLayout layout = CaptureLayout();
        float barTop = layout.Bar.Y;
        int cx0 = (int)MathF.Floor(Math.Clamp(x0, 0f, viewport.X));
        int cy0 = (int)MathF.Floor(Math.Clamp(y0, 0f, viewport.Y));
        int cx1 = (int)MathF.Ceiling(Math.Clamp(x1, 0f, viewport.X));
        int cy1 = (int)MathF.Ceiling(Math.Clamp(y1, 0f, MathF.Min(viewport.Y, barTop)));
        if (cx1 - cx0 < 2 || cy1 - cy0 < 2)
        {
            Console.WriteLine(
                $"[hud-capture] actorbox none: nothing of the figure is on screen above the bar " +
                $"(projected {x0:0},{y0:0},{x1:0},{y1:0} in {viewport.X}x{viewport.Y}, bar top {barTop:0})");
            return;
        }

        bool clipped = x0 < 0f || y0 < 0f || x1 > viewport.X || y1 > viewport.Y;
        bool underBar = y1 > barTop;
        Console.WriteLine(
            $"[hud-capture] actorbox {cx0},{cy0},{cx1},{cy1} (viewport {viewport.X}x{viewport.Y}, bar top {barTop:0}, " +
            $"{cx1 - cx0}x{cy1 - cy0} px, cornersBehindCamera {behind}/{points.Count}, equipmentCorners {equipmentCorners}, clippedByViewport {clipped}, clippedByBar {underBar}, " +
            $"feet {feet.X:0.00},{feet.Y:0.00},{feet.Z:0.00}, model {size.Width:0.00}x{size.Height:0.00}x{size.Depth:0.00} m, yaw {MathHelper.RadiansToDegrees(yaw):0})");
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
            HudCapturePointerPlan.RightClick(_capturePointerFrames, at);
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

    // Everything the actor draws, as world points: the authored box's eight corners, then every worn and
    // held box corner the renderer placed last draw (nothing extra for an unequipped actor). The equipment
    // corners were placed under the last drawn model matrix, so they move by however far the feet have
    // moved since (feet minus drawnFeet). Pure, so a test can hold the logged box to "contains every
    // equipped piece" without a window.
    internal static List<Vector3> BuildActorPoints(
        Vector3 feet, float yaw, WorldAssetDimensions size, IReadOnlyList<Vector3> equipmentCorners, Vector3 drawnFeet)
    {
        float cos = MathF.Cos(yaw);
        float sin = MathF.Sin(yaw);
        float halfW = size.Width * 0.5f;
        float halfD = size.Depth * 0.5f;
        List<Vector3> points = new(8 + equipmentCorners.Count);
        for (int corner = 0; corner < 8; corner++)
        {
            float lx = (corner & 1) == 0 ? -halfW : halfW;
            float lz = (corner & 2) == 0 ? -halfD : halfD;
            float ly = (corner & 4) == 0 ? 0f : size.Height;
            points.Add(feet + new Vector3((lx * cos) + (lz * sin), ly, (lz * cos) - (lx * sin)));
        }

        Vector3 moved = feet - drawnFeet;
        for (int i = 0; i < equipmentCorners.Count; i++)
        {
            points.Add(equipmentCorners[i] + moved);
        }

        return points;
    }

    // The screen bounds of a set of world points: the min and max of those in front of the camera, and
    // how many were behind it (all of them behind means no box). Pure, so a test can hold the box to
    // "never smaller than what is drawn" without a window.
    internal static void ProjectBounds(
        IReadOnlyList<Vector3> points, Matrix4 viewProjection, Vector2i viewport,
        out float x0, out float y0, out float x1, out float y1, out int behind)
    {
        x0 = float.MaxValue;
        y0 = float.MaxValue;
        x1 = float.MinValue;
        y1 = float.MinValue;
        behind = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (!TryProjectToScreen(viewProjection, points[i], viewport, out Vector2 screen, out bool inFront) || !inFront)
            {
                behind++;
                continue;
            }

            x0 = MathF.Min(x0, screen.X);
            y0 = MathF.Min(y0, screen.Y);
            x1 = MathF.Max(x1, screen.X);
            y1 = MathF.Max(y1, screen.Y);
        }
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

        if (_settings.DeveloperSettingsVisible && !wantSettings)
        {
            _settings.ToggleDeveloperSettings();
        }

        if (_settings.InventoryVisible && !wantInventory)
        {
            _settings.ToggleInventory();
        }

        if (wantInventory && !_settings.InventoryVisible)
        {
            _settings.ToggleInventory();
        }

        if (wantSettings && !_settings.DeveloperSettingsVisible)
        {
            _settings.ToggleDeveloperSettings();
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
