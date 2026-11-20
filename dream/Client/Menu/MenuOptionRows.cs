using Dream.Application;
using Dream.Client.CameraControl;
using Dream.Configuration;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using OpenTK.Windowing.Common;

namespace Dream.Client.Menu;

// The standard lines of the Options screen, wired to the live settings and the saved client config.
// Game builds the list once and hands it to the menus; a later feature adds its own rows to the list.
public static class MenuOptionRows
{
    // The frame caps the slider steps through; 0 is uncapped.
    public static readonly int[] MsaaSteps = [0, 2, 4];
    public static readonly int[] FrameCaps = [30, 60, 90, 120, 144, 240, FrameRateCap.Uncapped];

    public static readonly VSyncMode[] VSyncSteps = [VSyncMode.Off, VSyncMode.On, VSyncMode.Adaptive];

    public static readonly WindowMode[] WindowModeSteps = [WindowMode.Windowed, WindowMode.Borderless, WindowMode.Fullscreen];

    public const float UiScaleMin = 0.75f;
    public const float UiScaleMax = 4f;

    // Zoom speed slider: the wheel step ratio runs from ZoomStepMin to ZoomStepMax.
    public const float ZoomStepMin = 1.02f;
    public const float ZoomStepMax = 1.5f;

    public static readonly CameraConfineMode[] ConfineSteps =
        [CameraConfineMode.Off, CameraConfineMode.FullscreenOnly, CameraConfineMode.Always];

    // onFrameCapChanged re-applies the frame cap to the window; persist saves the client config.
    // onWindowModeChanged applies config.WindowMode to the window after the Display Mode row changes it (null: no row
    // effect beyond the config, as in tests).
    public static List<MenuOptionRow> CreateStandard(
        GameSettings settings,
        DesktopClientConfig config,
        Action onFrameCapChanged,
        Action persist,
        Action? onWindowModeChanged = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(onFrameCapChanged);
        ArgumentNullException.ThrowIfNull(persist);

        WindowMode? windowModeAtDragStart = null;
        return
        [
            // 0.75 stands for auto (the largest scale that fits the window); 1..4 snap to quarter steps.
            MenuOptionRow.Slider(
                "UI Scale",
                UiScaleMin,
                UiScaleMax,
                UiScale.Step,
                () => settings.UiScaleSetting <= 0f ? UiScaleMin : settings.UiScaleSetting,
                value =>
                {
                    settings.SetUiScale(value < 1f ? 0f : value);
                    config.UiScale = settings.UiScaleSetting;
                },
                FormatUiScale,
                persist),
            // The window may change the mode itself (F11), so the getter always reads the config. The setter only
            // writes the config; the window changes once, on release, and only if the mode differs from where the
            // drag began.
            MenuOptionRow.Slider(
                "Display Mode",
                0f,
                WindowModeSteps.Length - 1,
                1f,
                () => WindowModeIndex(config.WindowMode),
                value =>
                {
                    windowModeAtDragStart ??= config.WindowMode;
                    config.WindowMode = WindowModeSteps[Math.Clamp((int)MathF.Round(value), 0, WindowModeSteps.Length - 1)];
                },
                value => WindowModeDisplay(WindowModeSteps[Math.Clamp((int)MathF.Round(value), 0, WindowModeSteps.Length - 1)]),
                () =>
                {
                    bool changed = windowModeAtDragStart is { } start && start != config.WindowMode;
                    windowModeAtDragStart = null;
                    if (changed)
                    {
                        onWindowModeChanged?.Invoke();
                    }

                    persist();
                }),
            MenuOptionRow.Slider(
                "Max FPS",
                0f,
                FrameCaps.Length - 1,
                1f,
                () => FrameCapIndex(config.MaxFramesPerSecond),
                value =>
                {
                    config.MaxFramesPerSecond = FrameCaps[Math.Clamp((int)MathF.Round(value), 0, FrameCaps.Length - 1)];
                    onFrameCapChanged();
                },
                value => FrameRateCap.Describe(FrameCaps[Math.Clamp((int)MathF.Round(value), 0, FrameCaps.Length - 1)]),
                persist),
            MenuOptionRow.Slider(
                "VSync",
                0f,
                VSyncSteps.Length - 1,
                1f,
                () => VSyncIndex(config.VSync),
                value =>
                {
                    config.VSync = VSyncSteps[Math.Clamp((int)MathF.Round(value), 0, VSyncSteps.Length - 1)];
                    onFrameCapChanged();
                },
                value => VSyncDisplay(VSyncSteps[Math.Clamp((int)MathF.Round(value), 0, VSyncSteps.Length - 1)]),
                persist),
            // The sample count is a window-creation hint, so it cannot change under a running window.
            // The value is saved and takes effect the next time the game is launched; no apply callback.
            MenuOptionRow.Slider(
                "Anti-Aliasing (restart)",
                0f,
                MsaaSteps.Length - 1,
                1f,
                () => MsaaIndex(config.MsaaSamples),
                value => config.MsaaSamples = MsaaSteps[Math.Clamp((int)MathF.Round(value), 0, MsaaSteps.Length - 1)],
                value => MsaaSettings.Describe(MsaaSteps[Math.Clamp((int)MathF.Round(value), 0, MsaaSteps.Length - 1)]),
                persist),
            MenuOptionRow.Slider(
                "Damage Text Size",
                FloatingCombatTextTuning.MinSizeMultiplier,
                FloatingCombatTextTuning.MaxSizeMultiplier,
                0.05f,
                () => settings.FloatingCombatText.SizeMultiplier,
                value =>
                {
                    settings.ApplyFloatingCombatTextTuning(settings.FloatingCombatText with { SizeMultiplier = value });
                    config.FloatingCombatText = settings.FloatingCombatText;
                },
                value => $"{value:0.00}x",
                persist),
            // Display only. The HUD surface of a running game reads it at its start, so a change made
            // in the pause menu takes full effect in the next game.
            MenuOptionRow.Toggle(
                "Admin HUD (F3)",
                () => settings.AdminHudAvailable,
                value =>
                {
                    config.AdminHud = value;
                    settings.SetAdminHudAvailable(value);
                },
                persist)
        ];
    }

    // The tactical camera lines, shown on the Camera screen reached from Options. Every setter writes the
    // live settings (sanitised) and then the saved config.
    public static List<MenuOptionRow> CreateCameraRows(GameSettings settings, DesktopClientConfig config, Action persist)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(persist);

        void Apply(Func<TacticalCameraSettings, TacticalCameraSettings> change)
        {
            settings.SetTacticalCamera(change(settings.TacticalCamera));
            config.TacticalCamera = settings.TacticalCamera;
        }

        IReadOnlyList<int> snapSteps = TacticalCameraSettings.AllowedSnapSteps;
        return
        [
            MenuOptionRow.Toggle(
                "Edge Pan",
                () => settings.TacticalCamera.EdgePanEnabled,
                value => Apply(c => c with { EdgePanEnabled = value }),
                persist),
            MenuOptionRow.Slider(
                "Edge Margin",
                2f,
                48f,
                1f,
                () => settings.TacticalCamera.EdgeMarginPixels,
                value => Apply(c => c with { EdgeMarginPixels = value }),
                value => $"{value:0} px",
                persist),
            MenuOptionRow.Slider(
                "Pan Speed",
                4f,
                40f,
                1f,
                () => settings.TacticalCamera.PanSpeed,
                value => Apply(c => c with { PanSpeed = value }),
                value => $"{value:0} m/s",
                persist),
            MenuOptionRow.Slider(
                "Cursor Confine",
                0f,
                ConfineSteps.Length - 1,
                1f,
                () => ConfineIndex(settings.TacticalCamera.ConfineMode),
                value => Apply(c => c with { ConfineMode = ConfineSteps[Math.Clamp((int)MathF.Round(value), 0, ConfineSteps.Length - 1)] }),
                value => ConfineDisplay(ConfineSteps[Math.Clamp((int)MathF.Round(value), 0, ConfineSteps.Length - 1)]),
                persist),
            MenuOptionRow.Toggle(
                "Free Camera (Y)",
                () => settings.TacticalCamera.AnchorMode == CameraAnchorMode.Free,
                value => Apply(c => c with { AnchorMode = value ? CameraAnchorMode.Free : CameraAnchorMode.Follow }),
                persist),
            MenuOptionRow.Slider(
                "Return To Player",
                0f,
                10f,
                0.5f,
                () => settings.TacticalCamera.ReturnToPlayerSeconds,
                value => Apply(c => c with { ReturnToPlayerSeconds = value }),
                value => value <= 0f ? "Off" : $"{value:0.#} s",
                persist),
            MenuOptionRow.Slider(
                "Rotate Sensitivity",
                0.05f,
                1.5f,
                0.05f,
                () => settings.TacticalCamera.RotateDegreesPerPixel,
                value => Apply(c => c with { RotateDegreesPerPixel = value }),
                value => $"{value:0.00}",
                persist),
            MenuOptionRow.Slider(
                "Rotate Snap Step",
                0f,
                snapSteps.Count - 1,
                1f,
                () => SnapStepIndex(settings.TacticalCamera.RotateSnapStepDegrees),
                value => Apply(c => c with { RotateSnapStepDegrees = snapSteps[Math.Clamp((int)MathF.Round(value), 0, snapSteps.Count - 1)] }),
                value => $"{snapSteps[Math.Clamp((int)MathF.Round(value), 0, snapSteps.Count - 1)]} deg",
                persist),
            MenuOptionRow.Toggle(
                "Snap On Release",
                () => settings.TacticalCamera.SnapOnDragRelease,
                value => Apply(c => c with { SnapOnDragRelease = value }),
                persist),
            MenuOptionRow.Slider(
                "Zoom Speed",
                ZoomStepMin,
                ZoomStepMax,
                0.01f,
                () => settings.TacticalCamera.ZoomStepRatio,
                value => Apply(c => c with { ZoomStepRatio = value }),
                value => $"{(value - 1f) * 100f:0}%",
                persist),
            MenuOptionRow.Toggle(
                "Invert Zoom",
                () => settings.TacticalCamera.InvertZoom,
                value => Apply(c => c with { InvertZoom = value }),
                persist)
        ];
    }

    public static float ConfineIndex(CameraConfineMode mode) =>
        Math.Max(0, Array.IndexOf(ConfineSteps, Enum.IsDefined(mode) ? mode : CameraConfineMode.FullscreenOnly));

    public static string ConfineDisplay(CameraConfineMode mode) => mode switch
    {
        CameraConfineMode.Off => "Off",
        CameraConfineMode.Always => "Always",
        _ => "Fullscreen"
    };

    public static float SnapStepIndex(float degrees)
    {
        int best = 0;
        for (int i = 1; i < TacticalCameraSettings.AllowedSnapSteps.Count; i++)
        {
            if (MathF.Abs(TacticalCameraSettings.AllowedSnapSteps[i] - degrees) < MathF.Abs(TacticalCameraSettings.AllowedSnapSteps[best] - degrees))
            {
                best = i;
            }
        }

        return best;
    }

    public static float WindowModeIndex(WindowMode mode) =>
        Math.Max(0, Array.IndexOf(WindowModeSteps, mode));

    public static string WindowModeDisplay(WindowMode mode) => mode switch
    {
        WindowMode.Borderless => "Borderless",
        WindowMode.Fullscreen => "Fullscreen",
        _ => "Windowed"
    };

    public static float VSyncIndex(VSyncMode mode) =>
        Math.Max(0, Array.IndexOf(VSyncSteps, Enum.IsDefined(mode) ? mode : VSyncSettings.Default));

    public static string VSyncDisplay(VSyncMode mode) => mode switch
    {
        VSyncMode.On => "On",
        VSyncMode.Adaptive => "Adaptive",
        _ => "Off"
    };

    public static float MsaaIndex(int samples) => Array.IndexOf(MsaaSteps, MsaaSettings.Sanitize(samples));

    public static string FormatUiScale(float value) =>
        value < 1f ? "Auto" : $"{UiScale.Resolve(0, 0, value):0.##}x";

    // The step nearest to a configured cap; a cap between steps (an old config) shows the nearest one.
    public static float FrameCapIndex(int cap)
    {
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < FrameCaps.Length; i++)
        {
            int a = FrameCaps[i] == FrameRateCap.Uncapped ? int.MaxValue / 2 : FrameCaps[i];
            int b = cap == FrameRateCap.Uncapped ? int.MaxValue / 2 : cap;
            int distance = Math.Abs(a - b);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }
}
