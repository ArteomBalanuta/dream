using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Dream.Rendering.Ui;
using Image = SixLabors.ImageSharp.Image;

namespace Dream.Client.Scenes;

// DREAM_HUD_CAPTURE=<dir> DREAM_HUD_CAPTURE_SCRIPT=play,inventory,admin: after a warm-up, each step
// sets the HUD state, waits a few frames, reads the back buffer before the swap and writes
// <dir>/NN-<step>.png; then the window is asked to close. A verification tool for the real GL
// path. Off unless the directory variable is set; it changes no gameplay and sends nothing.
internal sealed class HudCaptureScript
{
    public const string DirectoryVariable = "DREAM_HUD_CAPTURE";
    public const string ScriptVariable = "DREAM_HUD_CAPTURE_SCRIPT";
    public const string StepPlay = "play";
    public const string StepInventory = "inventory";
    public const string StepSettings = "settings";
    public const string StepAdmin = "admin";

    // Frames before the first step: atlas decode, model loads and the first snapshots.
    public const int WarmupFrames = 300;

    // Frames between setting a state and reading it.
    public const int SettleFrames = 20;

    public const int MaxSteps = 16;

    private static readonly string[] KnownSteps = [StepPlay, StepInventory, StepSettings, StepAdmin];

    private HudCaptureScript(string directory, string[] steps)
    {
        Directory = directory;
        Steps = steps;
    }

    public string Directory { get; }

    public IReadOnlyList<string> Steps { get; }

    // Null unless the directory is set. Unknown step names are dropped; an empty script is "play".
    public static HudCaptureScript? Parse(string? directory, string? script)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        List<string> steps = [];
        foreach (string raw in (script ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string step = raw.ToLowerInvariant();
            if (Array.IndexOf(KnownSteps, step) >= 0 && steps.Count < MaxSteps)
            {
                steps.Add(step);
            }
        }

        if (steps.Count == 0)
        {
            steps.Add(StepPlay);
        }

        return new HudCaptureScript(directory.Trim(), steps.ToArray());
    }

    public static HudCaptureScript? FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(DirectoryVariable),
        Environment.GetEnvironmentVariable(ScriptVariable));

    public string FilePath(int index) => Path.Combine(Directory, $"{index + 1:00}-{Steps[index]}.png");
}

public partial class PlayScene
{
    private readonly HudCaptureScript? _hudCapture = HudCaptureScript.FromEnvironment();
    private int _hudCaptureFrames;
    private int _hudCaptureStep;
    private int _hudCaptureSettle;
    private bool _hudCaptureDone;

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
            Console.WriteLine("[hud-capture] done");
            unsafe
            {
                GLFW.SetWindowShouldClose(GLFW.GetCurrentContext(), true);
            }

            return;
        }

        if (_hudCaptureSettle == 0)
        {
            ApplyHudCaptureState(script.Steps[_hudCaptureStep]);
        }

        if (++_hudCaptureSettle <= HudCaptureScript.SettleFrames)
        {
            return;
        }

        string path = script.FilePath(_hudCaptureStep);
        try
        {
            WriteBackBufferPng(path);
            Console.WriteLine($"[hud-capture] wrote {path} surface={_render.CurrentHudSurface(_settings)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"[hud-capture] could not write {path}: {ex.Message}");
        }

        _hudCaptureStep++;
        _hudCaptureSettle = 0;
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

    private void WriteBackBufferPng(string path)
    {
        Vector2i size = _render.ViewportSize;
        int width = size.X;
        int height = size.Y;
        byte[] pixels = new byte[width * height * 4];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        // GL rows run bottom to top; the PNG runs top to bottom. The framebuffer's alpha is not
        // meaningful, so the image is written opaque.
        byte[] flipped = new byte[pixels.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(pixels, (height - 1 - y) * stride, flipped, y * stride, stride);
        }

        for (int i = 3; i < flipped.Length; i += 4)
        {
            flipped[i] = 255;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(flipped, width, height);
        image.SaveAsPng(path);
    }
}
