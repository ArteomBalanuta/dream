using System.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
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
//   wait:<frames>, snap, log  idle, capture, print the inventory
//   fight:<frames>            for that many frames, face the nearest enemy, close in and attack in combat
//                             mode (the real command path), then capture; for damage numbers and death
// <slot> is 0..15 for the bag, 100..103 or main|off|head|body for the equipment wells.
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

    public const int MaxSteps = 40;

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
        Settle
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

        if (_capturePhase == CapturePhase.Pointer && _capturePointerIndex < _capturePointerFrames.Count)
        {
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

                if (!HudCapturePointerPlan.TryBuild(spec, in layout, _captureLast.Position, menuRow, confirm, _capturePointerFrames))
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
            case CapturePhase.Idle:
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

        _hudCaptureStep++;
        _hudCaptureSettle = 0;
        _capturePhase = CapturePhase.Begin;
        _captureFightActive = false;
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
        float strafe = _captureFightStuckTicks > 150 ? (((_captureFightStuckTicks / 90) % 2) == 0 ? 1f : -1f) : 0f;

        return frame with
        {
            RequestedMode = new PlayerModeSwitchIntent(mode),
            MoveIntent = new PlayerMoveIntent(distance > 1.8f ? 1f : 0f, strafe, 0f, jump),
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
            sb.Append("  bag:");
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

        sb.Append("  inventory open: ").Append(_settings.InventoryVisible).Append(", settings open: ").Append(_settings.SettingsVisible).Append(", cursor: ").Append(DesiredCursorState);
        Console.WriteLine(sb.ToString());
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
