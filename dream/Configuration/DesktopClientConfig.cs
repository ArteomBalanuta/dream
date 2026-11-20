using Dream.Application;
using Dream.Client.CameraControl;
using Dream.Client.Startup;
using Dream.Rendering.Hud;
using Dream.Server;
using OpenTK.Windowing.Common;

namespace Dream.Configuration;

public sealed class DesktopClientConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = ServerBootstrap.DefaultPort;

    // The port Host on the Multiplayer screen listens on (config key host_port).
    public int HostPort { get; set; } = ServerBootstrap.DefaultPort;

    public string PlayerName { get; set; } = "dream-client";

    public float FieldOfViewDegrees { get; set; } = 70f;

    public float MouseSensitivity { get; set; } = 2.5f;

    public float NoclipSpeed { get; set; } = 18f;

    public CameraViewMode CameraViewMode { get; set; } = CameraViewMode.FirstPerson;

    public FloatingCombatTextTuning FloatingCombatText { get; set; } = FloatingCombatTextTuning.Default;

    // Render frame cap in FPS; 0 means uncapped.
    public int MaxFramesPerSecond { get; set; } = FrameRateCap.Default;

    // Swap interval: Off, On or Adaptive (config key vsync). DREAM_VSYNC overrides it for one run.
    public VSyncMode VSync { get; set; } = VSyncSettings.Default;

    // Multisample count of the window's default framebuffer (0, 2 or 4). Consumed at window creation,
    // so a change takes effect on the next launch.
    public int MsaaSamples { get; set; } = MsaaSettings.Default;

    // Makes the F3 key and the developer controls in Settings available. Display only: it never
    // reaches the server. DREAM_ADMIN_HUD overrides it for one run and is never saved.
    public bool AdminHud { get; set; }

    // HUD scale; 0 is auto (fit the window), other values snap to quarter steps in 1..4.
    public float UiScale { get; set; }

    // Tactical camera tuning (edge pan, rotation, zoom); keys camera_* in the text config.
    public TacticalCameraSettings TacticalCamera { get; set; } = TacticalCameraSettings.Default;

    // Sound on or off, and the 0..1 volume sliders (Dream.Client.Audio.AudioVolumes reads these).
    public bool AudioEnabled { get; set; } = true;

    public float VolumeMaster { get; set; } = 0.8f;

    public float VolumeSfx { get; set; } = 0.8f;

    public float VolumeMusic { get; set; } = 0.5f;

    public float VolumeUi { get; set; } = 0.7f;

    public ClientConnectionOptions ToConnectionOptions()
    {
        Sanitize();
        return new ClientConnectionOptions(Host, Port.ToString(), PlayerName);
    }

    public void ApplyConnectionOptions(ClientConnectionOptions options)
    {
        Host = options.Host;
        PlayerName = options.PlayerName;
        if (int.TryParse(options.PortText, out int port))
        {
            Port = port;
        }

        Sanitize();
    }

    public void CaptureRuntime(GameSettings settings)
    {
        FieldOfViewDegrees = settings.FieldOfViewDegrees;
        MouseSensitivity = settings.MouseSensitivity;
        NoclipSpeed = settings.NoclipSpeed;
        CameraViewMode = settings.CameraViewMode;
        FloatingCombatText = settings.FloatingCombatText;
        UiScale = settings.UiScaleSetting;
        TacticalCamera = settings.TacticalCamera;
        Sanitize();
    }

    public void Sanitize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? "dream-client" : PlayerName.Trim();
        Port = Port is < 1 or > 65535 ? ServerBootstrap.DefaultPort : Port;
        HostPort = HostPort is < 1 or > 65535 ? ServerBootstrap.DefaultPort : HostPort;
        FieldOfViewDegrees = SanitizeRange(FieldOfViewDegrees, 50f, 100f, 70f);
        MouseSensitivity = SanitizeRange(MouseSensitivity, 0.5f, 8f, 2.5f);
        NoclipSpeed = SanitizeRange(NoclipSpeed, GameSettings.MinNoclipSpeed, GameSettings.MaxNoclipSpeed, 18f);
        if (!Enum.IsDefined(CameraViewMode))
        {
            CameraViewMode = CameraViewMode.FirstPerson;
        }

        FloatingCombatText = FloatingCombatText.Clamp();
        MaxFramesPerSecond = FrameRateCap.Sanitize(MaxFramesPerSecond);
        if (!Enum.IsDefined(VSync))
        {
            VSync = VSyncSettings.Default;
        }

        MsaaSamples = MsaaSettings.Sanitize(MsaaSamples);
        UiScale = HudOptions.SanitizeUiScale(UiScale);
        TacticalCamera = (TacticalCamera ?? TacticalCameraSettings.Default).Sanitized();
        VolumeMaster = SanitizeVolume(VolumeMaster, 0.8f);
        VolumeSfx = SanitizeVolume(VolumeSfx, 0.8f);
        VolumeMusic = SanitizeVolume(VolumeMusic, 0.5f);
        VolumeUi = SanitizeVolume(VolumeUi, 0.7f);
    }

    // Math.Clamp leaves NaN unchanged (both of its comparisons are false), and a NaN field of view poisons
    // the projection matrix and is then written straight back to the file, so non-finite input falls back.
    private static float SanitizeRange(float value, float min, float max, float fallback)
    {
        return float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static float SanitizeVolume(float value, float fallback)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;
    }
}
