using System.Globalization;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Diagnostics;

public static class NetworkDiagnosticsLog
{
    public const string EnabledEnvironmentVariable = "DREAM_NET_DIAGNOSTICS";
    public const string PathEnvironmentVariable = "DREAM_NET_DIAGNOSTICS_PATH";

    private static readonly object Sync = new();
    private static readonly Lazy<bool> EnabledFlag = new(
        () => IsTruthy(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)));
    private static readonly NetworkDiagnosticsRateLimiter RateLimiter = new();
    private static bool _announced;

    // Set only by CaptureOnCurrentThread. It enables diagnostics for one thread and receives that
    // thread's lines instead of the console and file, so tests can read the exact text without
    // turning diagnostics on for code running in parallel on other threads.
    [ThreadStatic]
    private static Action<string, string>? t_captureSink;

    // Per-frame and per-snapshot callers check this before they build keys or messages.
    public static bool IsEnabled => t_captureSink != null || EnabledFlag.Value;

    public static string LogPath
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            return Path.Combine(AppContext.BaseDirectory, "logs", "network-diagnostics.log");
        }
    }

    public static bool ShouldLogEvery(string key, TimeSpan interval)
    {
        return IsEnabled && RateLimiter.ShouldLog(key, interval);
    }

    public static void Write(string category, string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        WriteEnabled(category, message);
    }

    public static void Write(string category, Func<string> messageFactory)
    {
        if (!IsEnabled)
        {
            return;
        }

        WriteEnabled(category, messageFactory());
    }

    public static void WriteEvery(string key, TimeSpan interval, string category, string message)
    {
        if (!ShouldLogEvery(key, interval))
        {
            return;
        }

        WriteEnabled(category, message);
    }

    public static void WriteEvery(string key, TimeSpan interval, string category, Func<string> messageFactory)
    {
        if (!ShouldLogEvery(key, interval))
        {
            return;
        }

        WriteEnabled(category, messageFactory());
    }

    // Callers pass a static lambda plus explicit state, so no closure is allocated per call.
    public static void Write<TState>(string category, TState state, Func<TState, string> messageFactory)
    {
        if (!IsEnabled)
        {
            return;
        }

        WriteEnabled(category, messageFactory(state));
    }

    // Callers pass a static lambda plus explicit state, so no closure is allocated per call. An
    // interpolated key is still built before this runs, so guard such calls with IsEnabled.
    public static void WriteEvery<TState>(
        string key,
        TimeSpan interval,
        string category,
        TState state,
        Func<TState, string> messageFactory)
    {
        if (!ShouldLogEvery(key, interval))
        {
            return;
        }

        WriteEnabled(category, messageFactory(state));
    }

    // Enables diagnostics on the calling thread only and sends its (category, message) pairs to
    // the sink instead of the console and the log file, until the returned scope is disposed.
    // Rate limiting still applies, through the process-wide limiter.
    public static IDisposable CaptureOnCurrentThread(Action<string, string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        CaptureScope scope = new(t_captureSink);
        t_captureSink = sink;
        return scope;
    }

    public static string DescribeActors(IReadOnlyList<ActorSnapshot> actors)
    {
        int playerCount = 0;
        int enemyCount = 0;
        List<string> playerFacts = [];
        foreach (ActorSnapshot actor in actors)
        {
            if (actor.Kind == CreatureKind.Player)
            {
                playerCount++;
                playerFacts.Add(
                    $"{actor.SessionId ?? "<none>"}#{actor.ActorId}/{actor.DisplayName ?? "<unnamed>"}" +
                    $"@{FormatVector(actor.Position)} hp={actor.CurrentHealth}/{actor.MaxHealth} alive={actor.IsAlive}");
            }
            else if (actor.Kind == CreatureKind.Enemy)
            {
                enemyCount++;
            }
        }

        string players = playerFacts.Count == 0
            ? "[]"
            : $"[{string.Join(", ", playerFacts)}]";
        return $"players={playerCount}{players} enemies={enemyCount} actors={actors.Count}";
    }

    public static string DescribeSnapshot(ServerSnapshotEnvelope snapshot)
    {
        return $"tick={snapshot.ServerTick} {DescribeActors(snapshot.Actors)} " +
            $"accepted=[{string.Join(", ", snapshot.LastAcceptedCommandSequences.Select(pair => $"{pair.Key}:{pair.Value}"))}] " +
            $"world={(snapshot.World == null ? "no" : "yes")} mutations={snapshot.WorldMutations.Count} damage={snapshot.DamageEvents.Count}";
    }

    public static string DescribeJoin(JoinAcceptedMessage accepted)
    {
        return $"session={accepted.SessionId} actor={accepted.ActorId} tick={accepted.CurrentServerTick} " +
            $"roster={DescribeRoster(accepted.EffectiveRoster)} initial={DescribeActors(accepted.EffectiveInitialActors)} " +
            $"world={(accepted.InitialWorld == null ? "no" : "yes")} inventory={(accepted.Inventory == null ? "no" : "yes")}";
    }

    public static string DescribeRoster(IReadOnlyList<PlayerRosterEntry> roster)
    {
        if (roster.Count == 0)
        {
            return "[]";
        }

        return $"[{string.Join(", ", roster.Select(entry => $"{entry.SessionId}#{entry.ActorId}/{entry.DisplayName}"))}]";
    }

    public static string FormatVector(OpenTK.Mathematics.Vector3 value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"({value.X:0.00},{value.Y:0.00},{value.Z:0.00})");
    }

    // Every line goes through here, and lines carry client-chosen text (player names above all). A control
    // character (a newline would start a forged log line, an escape sequence drives the terminal) or a
    // bidirectional formatting character (it reorders the text after it) is written as \uXXXX, so one
    // call is always exactly one line of plain text. Plain strings are returned as they are.
    public static string Sanitize(string text)
    {
        int bad = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (NeedsEscape(text[i]))
            {
                bad = i;
                break;
            }
        }

        if (bad < 0)
        {
            return text;
        }

        System.Text.StringBuilder builder = new(text.Length + 16);
        builder.Append(text, 0, bad);
        for (int i = bad; i < text.Length; i++)
        {
            char c = text[i];
            if (NeedsEscape(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    // Numeric on purpose, the literals would be invisible characters: U+2028 and U+2029 separators,
    // U+061C, U+200E and U+200F marks, U+202A to U+202E and U+2066 to U+2069 bidi controls.
    private static bool NeedsEscape(char c)
    {
        int code = c;
        return char.IsControl(c)
            || code is 0x2028 or 0x2029 or 0x061C or 0x200E or 0x200F
            || (code >= 0x202A && code <= 0x202E)
            || (code >= 0x2066 && code <= 0x2069);
    }

    private static void WriteEnabled(string category, string message)
    {
        category = Sanitize(category);
        // The collision report is multi-line by design and holds only server-made numbers, no client text.
        if (!string.Equals(category, Dream.Simulation.Players.Player.CollisionReportCategory, StringComparison.Ordinal))
        {
            message = Sanitize(message);
        }

        if (t_captureSink is Action<string, string> captureSink)
        {
            captureSink(category, message);
            return;
        }

        string line = $"[{DateTimeOffset.Now:O}] [netdiag] [{category}] [t{Environment.CurrentManagedThreadId}] {message}";
        lock (Sync)
        {
            AnnouncePathOnce();
            Console.WriteLine(line);
            try
            {
                string path = LogPath;
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[netdiag] [write-failed] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void AnnouncePathOnce()
    {
        if (_announced)
        {
            return;
        }

        _announced = true;
        Console.WriteLine($"[netdiag] enabled; writing to {LogPath}");
    }

    private sealed class CaptureScope(Action<string, string>? previousSink) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            t_captureSink = previousSink;
        }
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
