using System;
using System.IO;
using Dream.Rendering.Core;

namespace Dream.Assets;


public static class AssetPaths
{
    public const string RootEnvironmentVariable = "DREAM_ASSET_ROOT";

    private static readonly Lazy<string> CachedRoot = new(
        () => ResolveRoot(Environment.GetEnvironmentVariable, AppContext.BaseDirectory, Console.Error.WriteLine));

    // Read-only asset folders live here; DREAM_ASSET_ROOT moves them (intended: the source tree "dream" folder).
    // Writable state (database, desktop-client.txt) never follows it; see DataPaths.
    public static string RuntimeRoot => CachedRoot.Value;

    // A missing directory or one without a "shaders" folder is reported once and the exe directory is used.
    internal static string ResolveRoot(Func<string, string?> env, string baseDirectory, Action<string>? log)
    {
        string? value = env(RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return baseDirectory;
        }

        string root;
        try
        {
            root = Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            log?.Invoke($"[startup] {RootEnvironmentVariable} ignored ({value}): invalid path; using {baseDirectory}");
            return baseDirectory;
        }

        if (!Directory.Exists(root) || !Directory.Exists(Path.Combine(root, "shaders")))
        {
            log?.Invoke($"[startup] {RootEnvironmentVariable} ignored ({root}): directory or its shaders folder is missing; using {baseDirectory}");
            return baseDirectory;
        }

        log?.Invoke($"[startup] asset root: {root}");
        return root;
    }

    public static string ShadersDirectory => Path.Combine(RuntimeRoot, "shaders");

    public static string AnimationsDirectory => Path.Combine(RuntimeRoot, "animations");

    public static string FontsDirectory => Path.Combine(RuntimeRoot, "fonts");

    public static string AudioDirectory => Path.Combine(RuntimeRoot, "audio");

    public static string ConfigDirectory => Path.Combine(RuntimeRoot, "config");

    public static string UiDirectory => Path.Combine(RuntimeRoot, "ui");

    public static string Shader(string fileName) => Path.Combine(ShadersDirectory, fileName);

    public static string Animation(string fileName) => Path.Combine(AnimationsDirectory, fileName);

    public static string Font(string fileName) => Path.Combine(FontsDirectory, fileName);

    public static string Config(string fileName) => Path.Combine(ConfigDirectory, fileName);

    // relativePath is under the ui directory, e.g. "hud/hud.json".
    public static string Ui(string relativePath) => Path.Combine(UiDirectory, relativePath);
}
