using System.Globalization;
using System.IO;

public static class DesktopClientTextConfig
{
    public static DesktopClientConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Save(path, new DesktopClientConfig());
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

    public static string Serialize(DesktopClientConfig config)
    {
        config.Sanitize();
        return string.Join(Environment.NewLine, new[]
        {
            "# Desktop client startup + settings defaults",
            $"host={config.Host}",
            $"port={config.Port}",
            $"player_name={config.PlayerName}",
            $"field_of_view={Format(config.FieldOfViewDegrees)}",
            $"mouse_sensitivity={Format(config.MouseSensitivity)}",
            $"noclip_speed={Format(config.NoclipSpeed)}",
            $"strength={config.Strength}",
            $"attack_speed={Format(config.AttackSpeed)}",
            $"vitality={config.Vitality}",
            $"damage_text_size={Format(config.FloatingCombatText.SizeMultiplier)}",
            $"damage_text_speed={Format(config.FloatingCombatText.MotionSpeedMultiplier)}",
            $"damage_text_crit_depth={Format(config.FloatingCombatText.CriticalDepthMultiplier)}"
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

        if (key == "port")
        {
            if (int.TryParse(value, CultureInfo.InvariantCulture, out int port))
            {
                config.Port = port;
            }

            return;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
            {
                switch (key)
                {
                    case "strength":
                        config.Strength = integer;
                        break;
                    case "vitality":
                        config.Vitality = integer;
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
            case "strength":
                config.Strength = (int)MathF.Round(parsed);
                break;
            case "attack_speed":
                config.AttackSpeed = parsed;
                break;
            case "vitality":
                config.Vitality = (int)MathF.Round(parsed);
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
}
