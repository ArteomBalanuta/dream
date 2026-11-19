using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dream.Assets;
using Dream.Simulation.Characters;

namespace Dream.Client.Identity;

// Token file: {"version":1,"servers":{"local":"<token>","play.example.com:5057":"<token>"}}.
// One token per server key, so a hostile server cannot replay a token elsewhere. A corrupt file is
// never overwritten: it moves aside to .bad-<UTC stamp> and a new store starts. The token is never
// logged or put in an exception message.
public sealed class ClientIdentityStore : IClientIdentityStore
{
    public const string PathEnvironmentVariable = "DREAM_IDENTITY_FILE";
    public const string LocalServerKey = "local";
    private const int FileVersion = 1;

    // Serialises stores inside this process; the named mutex covers other windows.
    private static readonly object ProcessLock = new();

    private readonly string _path;

    public ClientIdentityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public static ClientIdentityStore ForCurrentUser()
    {
        return new ClientIdentityStore(ResolvePath(
            Environment.GetEnvironmentVariable,
            DataPaths.IdentityFile,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    }

    // Precedence: DREAM_IDENTITY_FILE, then the run data directory's file, then the per-user default.
    internal static string ResolvePath(Func<string, string?> env, string? dataDirIdentityFile, string localAppData)
    {
        string? overridePath = env(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        if (!string.IsNullOrWhiteSpace(dataDirIdentityFile))
        {
            return dataDirIdentityFile;
        }

        return Path.Combine(localAppData, "Dream", "account-tokens.json");
    }

    // Lowercase host:port, or the literal "local" for a loopback server. The host is canonical: brackets
    // and a trailing dot are dropped and an IP literal is written the way IPAddress prints it, so one
    // server never gets two tokens by how its address was typed.
    public static string ServerKeyFor(string host, int port, bool isLocalServer)
    {
        return isLocalServer
            ? LocalServerKey
            : $"{CanonicalHost(host)}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string CanonicalHost(string host)
    {
        string trimmed = host.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        trimmed = trimmed.TrimEnd('.');
        return System.Net.IPAddress.TryParse(trimmed, out System.Net.IPAddress? address)
            ? address.ToString().ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }

    // The file was written by a newer build: it is not corrupt, so it is neither read nor moved aside.
    private sealed class NewerFileException(string message) : IOException(message);

    // Another process holds the file's mutex past the wait. Its message is the one to show, not "check the folder".
    private sealed class FileInUseException(string message) : IOException(message);

    // How long to wait for another window holding the file; settable for tests.
    internal TimeSpan MutexWait { get; set; } = TimeSpan.FromSeconds(10);

    internal static string MutexNameFor(string fullPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        return "Dream.IdentityStore." + Convert.ToHexString(hash, 0, 8);
    }

    public string GetOrCreateToken(string serverKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        try
        {
            lock (ProcessLock)
            {
                using Mutex? mutex = TryAcquireMutex(out bool owned);
                if (mutex != null && !owned)
                {
                    // Another process holds the file for longer than the wait: do not write beside it.
                    throw new FileInUseException("The player identity file is in use by another process. Close the other Dream window and try again.");
                }

                try
                {
                    return GetOrCreateLocked(serverKey);
                }
                finally
                {
                    if (mutex != null && owned)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not NewerFileException and not FileInUseException
            && ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The inner exception is not chained: nothing from the file may reach a message.
            throw new IOException(
                $"The player identity file could not be saved at '{_path}'. Check the folder is writable, or set {PathEnvironmentVariable}.");
        }
    }

    private string GetOrCreateLocked(string serverKey)
    {
        JsonObject servers = LoadServers();
        if (servers[serverKey] is JsonValue existing
            && existing.TryGetValue(out string? stored)
            && AccountKey.TryFromToken(stored, out _))
        {
            return stored!;
        }

        string token = Base64UrlEncode(RandomNumberGenerator.GetBytes(AccountKey.TokenBytes));
        servers[serverKey] = token;
        Save(servers);
        return token;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private JsonObject LoadServers()
    {
        if (!File.Exists(_path))
        {
            return new JsonObject();
        }

        string text = File.ReadAllText(_path);
        if (IsNewerVersion(text))
        {
            throw new NewerFileException(
                $"The player identity file at '{_path}' was written by a newer version of Dream and was left as it is. Update the game, or set {PathEnvironmentVariable} to another file.");
        }

        if (TryParse(text, out JsonObject? servers))
        {
            return servers!;
        }

        MoveAside();
        return new JsonObject();
    }

    private static bool IsNewerVersion(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject root
                && root["version"] is JsonValue version
                && version.TryGetValue(out int v)
                && v > FileVersion;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool TryParse(string text, out JsonObject? servers)
    {
        servers = null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root
                || root["version"] is not JsonValue version
                || !version.TryGetValue(out int v)
                || v != FileVersion
                || root["servers"] is not JsonObject map)
            {
                return false;
            }

            JsonObject copy = new();
            foreach (KeyValuePair<string, JsonNode?> entry in map)
            {
                if (entry.Value is not JsonValue value || !value.TryGetValue(out string? entryToken) || entryToken == null)
                {
                    return false;
                }

                copy[entry.Key] = entryToken;
            }

            servers = copy;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void MoveAside()
    {
        string aside = $"{_path}.bad-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
        File.Move(_path, aside, overwrite: true);
        Console.Error.WriteLine($"Player identity file was unreadable and was moved to '{aside}'. A new identity file was started.");
    }

    private void Save(JsonObject servers)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            CreateDirectory(directory);
        }

        JsonObject root = new() { ["version"] = FileVersion, ["servers"] = servers };
        string temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = CreateTempFile(temp))
            {
                using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
                {
                    root.WriteTo(writer);
                }

                // On disk before the move makes it the token file: a crash must not leave an empty one.
                stream.Flush(true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void CreateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static FileStream CreateTempFile(string path)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private Mutex? TryAcquireMutex(out bool owned)
    {
        owned = false;
        try
        {
            Mutex mutex = new(false, MutexNameFor(_path));
            try
            {
                owned = mutex.WaitOne(MutexWait);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            return mutex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            return null;
        }
    }
}
