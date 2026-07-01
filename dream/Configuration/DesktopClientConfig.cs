using Dream.Application;
using Dream.Client.Startup;
using Dream.Rendering.Hud;
using Dream.Server;
using Dream.Simulation.Players;

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

    public int Strength { get; set; } = 10;

    public float AttackSpeed { get; set; } = 2f;

    public int Vitality { get; set; } = 10;

    public FloatingCombatTextTuning FloatingCombatText { get; set; } = FloatingCombatTextTuning.Default;

    public ClientConnectionOptions ToConnectionOptions()
    {
        Sanitize();
        return new ClientConnectionOptions(Host, Port.ToString(), PlayerName);
    }

    public PlayerCombatStats ToPlayerCombatStats()
    {
        Sanitize();
        return new PlayerCombatStats
        {
            Strength = Strength,
            AttackSpeed = AttackSpeed,
            Vitality = Vitality
        };
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

    public void CaptureRuntime(GameSettings settings, PlayerCombatStats stats)
    {
        FieldOfViewDegrees = settings.FieldOfViewDegrees;
        MouseSensitivity = settings.MouseSensitivity;
        NoclipSpeed = settings.NoclipSpeed;
        CameraViewMode = settings.CameraViewMode;
        FloatingCombatText = settings.FloatingCombatText;
        Strength = stats.Strength;
        AttackSpeed = stats.AttackSpeed;
        Vitality = stats.Vitality;
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

        Strength = Math.Clamp(Strength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength);
        AttackSpeed = Math.Clamp(AttackSpeed, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed);
        Vitality = Math.Clamp(Vitality, PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality);
        FloatingCombatText = FloatingCombatText.Clamp();
    }
}
