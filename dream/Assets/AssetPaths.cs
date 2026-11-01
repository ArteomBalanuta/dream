using System;
using System.IO;
using Dream.Rendering.Core;

namespace Dream.Assets;


public static class AssetPaths
{
    public static string RuntimeRoot => AppContext.BaseDirectory;

    public static string ShadersDirectory => Path.Combine(RuntimeRoot, "shaders");

    public static string AnimationsDirectory => Path.Combine(RuntimeRoot, "animations");

    public static string FontsDirectory => Path.Combine(RuntimeRoot, "fonts");

    public static string ConfigDirectory => Path.Combine(RuntimeRoot, "config");

    public static string UiDirectory => Path.Combine(RuntimeRoot, "ui");

    public static string Shader(string fileName) => Path.Combine(ShadersDirectory, fileName);

    public static string Animation(string fileName) => Path.Combine(AnimationsDirectory, fileName);

    public static string Font(string fileName) => Path.Combine(FontsDirectory, fileName);

    public static string Config(string fileName) => Path.Combine(ConfigDirectory, fileName);

    // relativePath is under the ui directory, e.g. "hud/hud.json".
    public static string Ui(string relativePath) => Path.Combine(UiDirectory, relativePath);
}
