using System.Globalization;
using System.IO;
using Dream.Application;
using Dream.Client.CameraControl;
using Dream.Simulation.World;

namespace Dream.Configuration;


public static class DesktopClientTextConfig
{
    public static DesktopClientConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            // Seeding the file is a convenience: a read-only install directory must still start the game
            // on the in-memory defaults rather than throw out of the Game constructor. Save creates the
            // directory itself.
            TrySave(path, new DesktopClientConfig());
        }

        if (!File.Exists(path))
        {
            DesktopClientConfig defaults = new();
            defaults.Sanitize();
            return defaults;
        }

        DesktopClientConfig config = new();
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            ApplyValue(config, parts[0], parts[1]);
        }

        config.Sanitize();
        return config;
    }

    public static void Save(string path, DesktopClientConfig config)
    {
        config.Sanitize();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(config));
    }

    /// <summary>
    /// Saves the config, reporting a write failure instead of throwing. A settings change may fail to
    /// persist (read-only install directory, the file held open by a backup or AV scanner), but the
    /// commit callbacks run inside the render pass, so a throw there would take the session down.
    /// </summary>
    public static bool TrySave(string path, DesktopClientConfig config)
    {
        try
        {
            Save(path, config);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[settings] could not save {path}: {ex.Message}");
            return false;
        }
    }

    public static string Serialize(DesktopClientConfig config)
    {
        config.Sanitize();
        return string.Join(Environment.NewLine, new[]
        {
            "# Desktop client startup + settings defaults",
            $"host={config.Host}",
            $"port={config.Port}",
            $"player_name={config.PlayerName}",
            $"host_port={config.HostPort}",
            $"field_of_view={Format(config.FieldOfViewDegrees)}",
            $"mouse_sensitivity={Format(config.MouseSensitivity)}",
            $"camera_view={SerializeCameraViewMode(config.CameraViewMode)}",
            $"noclip_speed={Format(config.NoclipSpeed)}",
            $"damage_text_size={Format(config.FloatingCombatText.SizeMultiplier)}",
            $"damage_text_speed={Format(config.FloatingCombatText.MotionSpeedMultiplier)}",
            $"damage_text_crit_depth={Format(config.FloatingCombatText.CriticalDepthMultiplier)}",
            $"max_fps={config.MaxFramesPerSecond}",
            $"vsync={VSyncSettings.Describe(config.VSync)}",
            $"msaa={config.MsaaSamples}",
            $"admin_hud={(config.AdminHud ? 1 : 0)}",
            $"ui_scale={Format(config.UiScale)}",
            $"camera_edge_pan={(config.TacticalCamera.EdgePanEnabled ? 1 : 0)}",
            $"camera_edge_margin={Format(config.TacticalCamera.EdgeMarginPixels)}",
            $"camera_pan_speed={Format(config.TacticalCamera.PanSpeed)}",
            $"camera_confine={SerializeConfineMode(config.TacticalCamera.ConfineMode)}",
            $"camera_anchor={(config.TacticalCamera.AnchorMode == CameraAnchorMode.Free ? "free" : "follow")}",
            $"camera_return_seconds={Format(config.TacticalCamera.ReturnToPlayerSeconds)}",
            $"camera_rotate_sensitivity={Format(config.TacticalCamera.RotateDegreesPerPixel)}",
            $"camera_snap_step={Format(config.TacticalCamera.RotateSnapStepDegrees)}",
            $"camera_snap_on_release={(config.TacticalCamera.SnapOnDragRelease ? 1 : 0)}",
            $"camera_zoom_step={Format(config.TacticalCamera.ZoomStepRatio)}",
            $"camera_invert_zoom={(config.TacticalCamera.InvertZoom ? 1 : 0)}",
            $"audio_enabled={(config.AudioEnabled ? 1 : 0)}",
            $"volume_master={Format(config.VolumeMaster)}",
            $"volume_sfx={Format(config.VolumeSfx)}",
            $"volume_music={Format(config.VolumeMusic)}",
            $"volume_ui={Format(config.VolumeUi)}"
        }) + Environment.NewLine;
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void ApplyValue(DesktopClientConfig config, string key, string value)
    {
        switch (key)
        {
            case "host":
                config.Host = value;
                return;
            case "player_name":
                config.PlayerName = value;
                return;
        }

        if (key == "host_port")
        {
            if (int.TryParse(value, CultureInfo.InvariantCulture, out int hostPort))
            {
                config.HostPort = hostPort;
            }

            return;
        }

        if (key == "port")
        {
            if (int.TryParse(value, CultureInfo.InvariantCulture, out int port))
            {
                config.Port = port;
            }

            return;
        }

        if (key == "admin_hud")
        {
            config.AdminHud = HudOptions.ParseFlag(value) ?? false;
            return;
        }

        if (key == "audio_enabled")
        {
            config.AudioEnabled = HudOptions.ParseFlag(value) ?? true;
            return;
        }

        if (key == "vsync")
        {
            config.VSync = VSyncSettings.Parse(value) ?? VSyncSettings.Default;
            return;
        }

        if (ApplyCameraKey(config, key, value))
        {
            return;
        }

        if (key == "camera_view")
        {
            config.CameraViewMode = ParseCameraViewMode(value);
            return;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
            {
                switch (key)
                {
                    case "max_fps":
                        config.MaxFramesPerSecond = integer;
                        break;
                    case "msaa":
                        config.MsaaSamples = integer;
                        break;
                }
            }

            return;
        }

        switch (key)
        {
            case "field_of_view":
                config.FieldOfViewDegrees = parsed;
                break;
            case "mouse_sensitivity":
                config.MouseSensitivity = parsed;
                break;
            case "noclip_speed":
                config.NoclipSpeed = parsed;
                break;
            case "max_fps":
                config.MaxFramesPerSecond = (int)MathF.Round(parsed);
                break;
            case "msaa":
                config.MsaaSamples = (int)MathF.Round(parsed);
                break;
            case "ui_scale":
                config.UiScale = parsed;
                break;
            case "volume_master":
                config.VolumeMaster = parsed;
                break;
            case "volume_sfx":
                config.VolumeSfx = parsed;
                break;
            case "volume_music":
                config.VolumeMusic = parsed;
                break;
            case "volume_ui":
                config.VolumeUi = parsed;
                break;
            case "damage_text_size":
                config.FloatingCombatText = config.FloatingCombatText with { SizeMultiplier = parsed };
                break;
            case "damage_text_speed":
                config.FloatingCombatText = config.FloatingCombatText with { MotionSpeedMultiplier = parsed };
                break;
            case "damage_text_crit_depth":
                config.FloatingCombatText = config.FloatingCombatText with { CriticalDepthMultiplier = parsed };
                break;
        }
    }

    // The camera_* keys of the tactical camera. Returns true when the key belongs to it (even if the value was
    // unusable: the setting then keeps its default, and Sanitize pulls the rest into range).
    private static bool ApplyCameraKey(DesktopClientConfig config, string key, string value)
    {
        TacticalCameraSettings camera = config.TacticalCamera;
        bool Number(out float parsed) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);

        switch (key)
        {
            case "camera_edge_pan":
                config.TacticalCamera = camera with { EdgePanEnabled = HudOptions.ParseFlag(value) ?? true };
                return true;
            case "camera_snap_on_release":
                config.TacticalCamera = camera with { SnapOnDragRelease = HudOptions.ParseFlag(value) ?? false };
                return true;
            case "camera_invert_zoom":
                config.TacticalCamera = camera with { InvertZoom = HudOptions.ParseFlag(value) ?? false };
                return true;
            case "camera_confine":
                config.TacticalCamera = camera with { ConfineMode = ParseConfineMode(value) };
                return true;
            case "camera_anchor":
                config.TacticalCamera = camera with
                {
                    AnchorMode = value.Trim().Equals("free", StringComparison.OrdinalIgnoreCase)
                        ? CameraAnchorMode.Free
                        : CameraAnchorMode.Follow
                };
                return true;
            case "camera_edge_margin":
                if (Number(out float margin)) { config.TacticalCamera = camera with { EdgeMarginPixels = margin }; }
                return true;
            case "camera_pan_speed":
                if (Number(out float speed)) { config.TacticalCamera = camera with { PanSpeed = speed }; }
                return true;
            case "camera_return_seconds":
                if (Number(out float seconds)) { config.TacticalCamera = camera with { ReturnToPlayerSeconds = seconds }; }
                return true;
            case "camera_rotate_sensitivity":
                if (Number(out float sensitivity)) { config.TacticalCamera = camera with { RotateDegreesPerPixel = sensitivity }; }
                return true;
            case "camera_snap_step":
                if (Number(out float step)) { config.TacticalCamera = camera with { RotateSnapStepDegrees = step }; }
                return true;
            case "camera_zoom_step":
                if (Number(out float zoom)) { config.TacticalCamera = camera with { ZoomStepRatio = zoom }; }
                return true;
            default:
                return false;
        }
    }

    private static CameraConfineMode ParseConfineMode(string value)
    {
        return value.Trim().Replace("-", "_", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() switch
        {
            "off" or "none" => CameraConfineMode.Off,
            "always" or "on" => CameraConfineMode.Always,
            _ => CameraConfineMode.FullscreenOnly
        };
    }

    private static string SerializeConfineMode(CameraConfineMode mode) => mode switch
    {
        CameraConfineMode.Off => "off",
        CameraConfineMode.Always => "always",
        _ => "fullscreen"
    };

    private static CameraViewMode ParseCameraViewMode(string value)
    {
        return value.Trim().Replace("-", "_", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() switch
        {
            "third_person" or "thirdperson" or "3rd_person" or "3rd" => CameraViewMode.ThirdPerson,
            "isometric" or "iso" => CameraViewMode.Isometric,
            _ => CameraViewMode.FirstPerson
        };
    }

    private static string SerializeCameraViewMode(CameraViewMode mode)
    {
        return mode switch
        {
            CameraViewMode.ThirdPerson => "third_person",
            CameraViewMode.Isometric => "isometric",
            _ => "first_person"
        };
    }
}
