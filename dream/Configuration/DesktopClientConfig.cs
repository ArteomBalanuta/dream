using Dream.Application;
using Dream.Client.Startup;
using Dream.Rendering.Hud;
using Dream.Server;

namespace Dream.Configuration;

public sealed class DesktopClientConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = ServerBootstrap.DefaultPort;

    public string PlayerName { get; set; } = "dream-client";

    public float FieldOfViewDegrees { get; set; } = 70f;

    public float MouseSensitivity { get; set; } = 2.5f;

    public float NoclipSpeed { get; set; } = 18f;

    public CameraViewMode CameraViewMode { get; set; } = CameraViewMode.FirstPerson;

    public FloatingCombatTextTuning FloatingCombatText { get; set; } = FloatingCombatTextTuning.Default;

    // Render frame cap in FPS; 0 means uncapped.
    public int MaxFramesPerSecond { get; set; } = FrameRateCap.Default;

    // Makes the F3 key and the developer controls in Settings available. Display only: it never
    // reaches the server. DREAM_ADMIN_HUD overrides it for one run and is never saved.
    public bool AdminHud { get; set; }

    // HUD scale; 0 is auto (fit the window), other values snap to quarter steps in 1..4.
    public float UiScale { get; set; }

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
        Sanitize();
    }

    public void Sanitize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? "dream-client" : PlayerName.Trim();
        Port = Port is < 1 or > 65535 ? ServerBootstrap.DefaultPort : Port;
        FieldOfViewDegrees = Math.Clamp(FieldOfViewDegrees, 50f, 100f);
        MouseSensitivity = Math.Clamp(MouseSensitivity, 0.5f, 8f);
        NoclipSpeed = Math.Clamp(NoclipSpeed, GameSettings.MinNoclipSpeed, GameSettings.MaxNoclipSpeed);
        if (!Enum.IsDefined(CameraViewMode))
        {
            CameraViewMode = CameraViewMode.FirstPerson;
        }

        FloatingCombatText = FloatingCombatText.Clamp();
        MaxFramesPerSecond = FrameRateCap.Sanitize(MaxFramesPerSecond);
        UiScale = HudOptions.SanitizeUiScale(UiScale);
    }
}
