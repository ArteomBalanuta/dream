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

    public static bool IsEnabled => EnabledFlag.Value;

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

    private static void WriteEnabled(string category, string message)
    {
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

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
