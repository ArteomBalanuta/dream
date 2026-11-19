using Dream.Application;
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

    public const float UiScaleMin = 0.75f;
    public const float UiScaleMax = 4f;

    // onFrameCapChanged re-applies the frame cap to the window; persist saves the client config.
    public static List<MenuOptionRow> CreateStandard(
        GameSettings settings,
        DesktopClientConfig config,
        Action onFrameCapChanged,
        Action persist)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(onFrameCapChanged);
        ArgumentNullException.ThrowIfNull(persist);

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
