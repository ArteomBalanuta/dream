using System;
using System.IO;

public static class AssetPaths
{
    public static string RuntimeRoot => AppContext.BaseDirectory;

    public static string ShadersDirectory => Path.Combine(RuntimeRoot, "shaders");

    public static string AnimationsDirectory => Path.Combine(RuntimeRoot, "animations");

    public static string FontsDirectory => Path.Combine(RuntimeRoot, "fonts");

    public static string Shader(string fileName) => Path.Combine(ShadersDirectory, fileName);

    public static string Animation(string fileName) => Path.Combine(AnimationsDirectory, fileName);

    public static string Font(string fileName) => Path.Combine(FontsDirectory, fileName);
}
