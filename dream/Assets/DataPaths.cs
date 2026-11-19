using System;
using System.IO;

namespace Dream.Assets;

// Writable per-run state. DREAM_DATA_DIR moves the database, its lock files, desktop-client.txt and the
// default identity file; unset keeps the exe-relative "config" folder. It never follows DREAM_ASSET_ROOT.
public static class DataPaths
{
    public const string EnvironmentVariable = "DREAM_DATA_DIR";

    private const string DatabaseFileName = "player-profiles.db";
    private const string DesktopClientFileName = "desktop-client.txt";
    private const string IdentityFileName = "account-tokens.json";

    private static readonly Lazy<string?> CachedDirectory = new(
        () => ResolveDirectory(Environment.GetEnvironmentVariable, Console.Error.WriteLine));

    public static string Database => DatabasePath(CachedDirectory.Value, AppContext.BaseDirectory);

    public static string DesktopClientConfig => DesktopClientPath(CachedDirectory.Value, AppContext.BaseDirectory);

    // Null means "use the per-user default".
    public static string? IdentityFile => IdentityPath(CachedDirectory.Value);

    internal static string DatabasePath(string? dataDirectory, string baseDirectory) =>
        Path.Combine(dataDirectory ?? Path.Combine(baseDirectory, "config"), DatabaseFileName);

    internal static string DesktopClientPath(string? dataDirectory, string baseDirectory) =>
        Path.Combine(dataDirectory ?? Path.Combine(baseDirectory, "config"), DesktopClientFileName);

    internal static string? IdentityPath(string? dataDirectory) =>
        dataDirectory == null ? null : Path.Combine(dataDirectory, IdentityFileName);

    // Null when unset. Invalid values throw rather than silently falling back to the shared bin folder.
    internal static string? ResolveDirectory(Func<string, string?> env, Action<string>? log)
    {
        string? value = env(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        // A drive qualifier ("G:") is the only colon allowed anywhere in the path.
        string rest = value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' ? value[2..] : value;
        if (rest.Contains(':'))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} contains a colon: {value}");
        }

        string full;
        try
        {
            full = Path.GetFullPath(value);
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
            or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is not a usable directory ({value}): {ex.Message}", ex);
        }

        log?.Invoke($"[startup] data dir: {full}");
        return full;
    }
}
