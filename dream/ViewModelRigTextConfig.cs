using System;
using System.Globalization;
using System.IO;
using OpenTK.Mathematics;

public static class ViewModelRigTextConfig
{
    public static ViewModelRigSettings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, Serialize(ViewModelRigSettings.CreateDefault()));
        }

        ViewModelRigSettings settings = ViewModelRigSettings.CreateDefault();
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

            ApplyValue(settings, parts[0], parts[1]);
        }

        return settings;
    }

    public static string Serialize(ViewModelRigSettings settings)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Viewmodel rig startup defaults",
            $"body_anchor_spread={Format(settings.BodyAnchor.HorizontalSpread)}",
            $"body_anchor_drop={Format(settings.BodyAnchor.VerticalDrop)}",
            $"body_anchor_forward_pull={Format(settings.BodyAnchor.ForwardPull)}",
            $"held_item_position_x={Format(settings.HeldItem.PositionOffset.X)}",
            $"held_item_position_y={Format(settings.HeldItem.PositionOffset.Y)}",
            $"held_item_position_z={Format(settings.HeldItem.PositionOffset.Z)}",
            $"held_item_rotation_x={Format(settings.HeldItem.RotationOffsetDegrees.X)}",
            $"held_item_rotation_y={Format(settings.HeldItem.RotationOffsetDegrees.Y)}",
            $"held_item_rotation_z={Format(settings.HeldItem.RotationOffsetDegrees.Z)}"
        }) + Environment.NewLine;
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void ApplyValue(ViewModelRigSettings settings, string key, string value)
    {
        if (!float.TryParse(value, CultureInfo.InvariantCulture, out float parsed))
        {
            return;
        }

        switch (key)
        {
            case "body_anchor_spread":
                settings.BodyAnchor.HorizontalSpread = parsed;
                break;
            case "body_anchor_drop":
                settings.BodyAnchor.VerticalDrop = parsed;
                break;
            case "body_anchor_forward_pull":
                settings.BodyAnchor.ForwardPull = parsed;
                break;
            case "held_item_position_x":
                settings.HeldItem.PositionOffset = new Vector3(
                    parsed,
                    settings.HeldItem.PositionOffset.Y,
                    settings.HeldItem.PositionOffset.Z);
                break;
            case "held_item_position_y":
                settings.HeldItem.PositionOffset = new Vector3(
                    settings.HeldItem.PositionOffset.X,
                    parsed,
                    settings.HeldItem.PositionOffset.Z);
                break;
            case "held_item_position_z":
                settings.HeldItem.PositionOffset = new Vector3(
                    settings.HeldItem.PositionOffset.X,
                    settings.HeldItem.PositionOffset.Y,
                    parsed);
                break;
            case "held_item_rotation_x":
                settings.HeldItem.RotationOffsetDegrees = new Vector3(
                    parsed,
                    settings.HeldItem.RotationOffsetDegrees.Y,
                    settings.HeldItem.RotationOffsetDegrees.Z);
                break;
            case "held_item_rotation_y":
                settings.HeldItem.RotationOffsetDegrees = new Vector3(
                    settings.HeldItem.RotationOffsetDegrees.X,
                    parsed,
                    settings.HeldItem.RotationOffsetDegrees.Z);
                break;
            case "held_item_rotation_z":
                settings.HeldItem.RotationOffsetDegrees = new Vector3(
                    settings.HeldItem.RotationOffsetDegrees.X,
                    settings.HeldItem.RotationOffsetDegrees.Y,
                    parsed);
                break;
        }
    }
}
