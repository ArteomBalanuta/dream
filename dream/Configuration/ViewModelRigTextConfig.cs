using System;
using System.Globalization;
using System.IO;
using OpenTK.Mathematics;
using Dream.Rendering.ViewModels;
using Dream.Simulation.World;

namespace Dream.Configuration;


public static class ViewModelRigTextConfig
{
    public static ViewModelRigSettings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            // Seeding the file is a convenience: a read-only install directory must still start the game
            // on the in-memory defaults rather than throw out of the Game constructor.
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, Serialize(ViewModelRigSettings.CreateDefault()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                Console.Error.WriteLine($"[settings] could not write {path}: {ex.Message}");
                return ViewModelRigSettings.CreateDefault();
            }
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
        // "NaN", "Infinity" and "-Infinity" all parse successfully. Keeping one would make the held-item
        // model matrix NaN and the weapon or torch would silently vanish for the whole run, so the
        // default for that key is kept instead.
        if (!float.TryParse(value, CultureInfo.InvariantCulture, out float parsed) || !float.IsFinite(parsed))
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
