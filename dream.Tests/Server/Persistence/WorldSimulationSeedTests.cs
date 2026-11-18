using System.Reflection;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence;

public sealed class WorldSimulationSeedTests
{
    [Fact]
    public void NewWorld_GetsNonDefaultSeed_AndReloadKeepsIt()
    {
        using TempWorld world = new();

        ulong created;
        using (ServerPersistenceBundle first = world.OpenBundle())
        {
            Assert.True(first.SimulationSeed.HasValue);
            created = first.SimulationSeed.Value;
        }

        using ServerPersistenceBundle reloaded = world.OpenBundle();

        Assert.NotEqual(WorldSessionRuntime.DefaultSimulationSeed, created);
        Assert.Equal(created, reloaded.SimulationSeed);
        Assert.True(new SqliteWorldMetadataRepository(reloaded.Schema).TryGetSimulationSeed(out ulong stored));
        Assert.Equal(created, stored);
    }

    [Fact]
    public void TwoNewWorlds_GetDifferentSeeds()
    {
        using TempWorld left = new();
        using TempWorld right = new();
        using ServerPersistenceBundle leftBundle = left.OpenBundle();
        using ServerPersistenceBundle rightBundle = right.OpenBundle();

        Assert.NotEqual(leftBundle.SimulationSeed, rightBundle.SimulationSeed);
    }

    [Fact]
    public void GenerateSimulationSeed_NeverReturnsDefaultSeed()
    {
        HashSet<ulong> seeds = [];
        for (int i = 0; i < 64; i++)
        {
            ulong seed = SqliteWorldMetadataRepository.GenerateSimulationSeed();
            Assert.NotEqual(WorldSessionRuntime.DefaultSimulationSeed, seed);
            seeds.Add(seed);
        }

        Assert.Equal(64, seeds.Count);
    }

    [Fact]
    public void OldDatabaseWithoutMetadataTable_GetsSeedOnFirstLoad_AndItStaysStable()
    {
        using TempWorld world = new();
        world.CreateLegacyDatabaseWithoutMetadataTable();
        Assert.False(world.TableExists("world_metadata"));

        List<string> firstLog = [];
        ulong firstSeed;
        using (ServerPersistenceBundle first = world.OpenBundle(log: firstLog.Add))
        {
            firstSeed = first.SimulationSeed!.Value;
        }

        List<string> secondLog = [];
        using ServerPersistenceBundle second = world.OpenBundle(log: secondLog.Add);

        Assert.True(world.TableExists("world_metadata"));
        Assert.NotEqual(WorldSessionRuntime.DefaultSimulationSeed, firstSeed);
        Assert.Equal(firstSeed, second.SimulationSeed);
        Assert.Contains(firstLog, line => line.Contains("generated", StringComparison.Ordinal));
        Assert.DoesNotContain(secondLog, line => line.Contains("generated", StringComparison.Ordinal));
    }

    [Fact]
    public void DatabaseWithTableButNoSeedRow_GetsSeedOnFirstLoad()
    {
        using TempWorld world = new();
        ulong original;
        using (ServerPersistenceBundle first = world.OpenBundle())
        {
            original = first.SimulationSeed!.Value;
        }

        world.Execute("UPDATE worlds SET seed = NULL;");

        ulong regenerated;
        using (ServerPersistenceBundle second = world.OpenBundle())
        {
            regenerated = second.SimulationSeed!.Value;
        }

        using ServerPersistenceBundle third = world.OpenBundle();

        Assert.NotEqual(original, regenerated);
        Assert.Equal(regenerated, third.SimulationSeed);
    }

    [Fact]
    public void GetOrCreateSimulationSeed_UsesGeneratorOnlyOnce()
    {
        using TempWorld world = new();
        using ServerPersistenceBundle bundle = world.OpenBundle();
        world.Execute("UPDATE worlds SET seed = NULL;");
        SqliteWorldMetadataRepository repository = new(bundle.Schema);
        int calls = 0;

        WorldSimulationSeed created = repository.GetOrCreateSimulationSeed(() => { calls++; return 1234UL; });
        WorldSimulationSeed loaded = repository.GetOrCreateSimulationSeed(() => { calls++; return 5678UL; });

        Assert.Equal(new WorldSimulationSeed(1234UL, Created: true), created);
        Assert.Equal(new WorldSimulationSeed(1234UL, Created: false), loaded);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CorruptStoredSeed_FailsLoudlyInsteadOfRegenerating()
    {
        using TempWorld world = new();
        using (world.OpenBundle())
        {
        }

        world.Execute("UPDATE worlds SET seed = 'not-a-seed';");

        Assert.Throws<InvalidDataException>(() => world.OpenBundle());
    }

    [Fact]
    public void PersistentHost_RuntimeRollsWithStoredSeed()
    {
        using TempWorld world = new();
        ServerPersistenceBundle bundle = world.OpenBundle();
        ulong expected = bundle.SimulationSeed!.Value;

        using DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);

        Assert.NotNull(host.Runtime);
        Assert.Equal(expected, host.Runtime.SimulationSeed);
    }

    [Fact]
    public void HostWithoutPersistence_UsesDefaultSimulationSeed()
    {
        using DedicatedServerHost authoritative = ServerBootstrap.CreateAuthoritativeHost(persistence: null);
        using DedicatedServerHost attached = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        using RuntimeAuthoritativeSessionHost runtimeHost = RuntimeAuthoritativeSessionHost.CreateDefault();

        Assert.Equal(WorldSessionRuntime.DefaultSimulationSeed, authoritative.Runtime!.SimulationSeed);
        Assert.Equal(WorldSessionRuntime.DefaultSimulationSeed, attached.Runtime!.SimulationSeed);
        Assert.Equal(WorldSessionRuntime.DefaultSimulationSeed, runtimeHost.Runtime.SimulationSeed);
    }

    [Theory]
    [InlineData("12345", 12345UL)]
    [InlineData("  42  ", 42UL)]
    [InlineData("0x10", 16UL)]
    [InlineData("0XFFFFFFFFFFFFFFFF", ulong.MaxValue)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    [InlineData("0", 0UL)]
    public void TryParseSimulationSeed_AcceptsDecimalAndHex(string value, ulong expected)
    {
        Assert.True(ServerBootstrap.TryParseSimulationSeed(value, out ulong seed));
        Assert.Equal(expected, seed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("abc")]
    [InlineData("0x")]
    [InlineData("0xZZ")]
    [InlineData("18446744073709551616")]
    [InlineData("1.5")]
    public void TryParseSimulationSeed_RejectsInvalidValues(string? value)
    {
        Assert.False(ServerBootstrap.TryParseSimulationSeed(value, out _));
    }

    [Fact]
    public void ResolveSimulationSeed_ValidOverrideWins_AndIsLogged()
    {
        List<string> log = [];

        ulong seed = ServerBootstrap.ResolveSimulationSeed(storedSeed: 7UL, overrideValue: "0x2A", log.Add);

        Assert.Equal(42UL, seed);
        Assert.Single(log);
        Assert.Contains(ServerBootstrap.SimulationSeedEnvironmentVariable, log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSimulationSeed_InvalidOverrideIsIgnoredWithLogLine()
    {
        List<string> log = [];

        ulong seed = ServerBootstrap.ResolveSimulationSeed(storedSeed: 7UL, overrideValue: "not-a-number", log.Add);

        Assert.Equal(7UL, seed);
        Assert.Single(log);
        Assert.Contains("ignoring", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSimulationSeed_UnsetOverrideKeepsStoredSeedSilently()
    {
        List<string> log = [];

        Assert.Equal(7UL, ServerBootstrap.ResolveSimulationSeed(7UL, null, log.Add));
        Assert.Equal(7UL, ServerBootstrap.ResolveSimulationSeed(7UL, "  ", log.Add));
        Assert.Empty(log);
    }

    [Fact]
    public void OverrideAppliesToRunButIsNeverStored()
    {
        using TempWorld world = new();
        ulong stored;
        using (ServerPersistenceBundle first = world.OpenBundle())
        {
            stored = first.SimulationSeed!.Value;
        }

        ulong overrideSeed = stored == 99UL ? 100UL : 99UL;
        using (ServerPersistenceBundle overridden = world.OpenBundle(simulationSeedOverride: overrideSeed.ToString()))
        {
            Assert.Equal(overrideSeed, overridden.SimulationSeed);
            using DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(overridden);
            Assert.Equal(overrideSeed, host.Runtime!.SimulationSeed);
        }

        // One bundle at a time: an open bundle holds its world's lease.
        using (ServerPersistenceBundle afterwards = world.OpenBundle())
        {
            Assert.Equal(stored, afterwards.SimulationSeed);
        }

        using ServerPersistenceBundle invalid = world.OpenBundle(simulationSeedOverride: "garbage");
        Assert.Equal(stored, invalid.SimulationSeed);
    }

    [Fact]
    public void SeedValueIsNeverWrittenToLog()
    {
        using TempWorld world = new();
        List<string> log = [];
        using ServerPersistenceBundle bundle = world.OpenBundle(log: log.Add);
        string seedText = bundle.SimulationSeed!.Value.ToString();

        Assert.NotEmpty(log);
        Assert.DoesNotContain(log, line => line.Contains(seedText, StringComparison.Ordinal));
    }

    [Fact]
    public void NoProtocolMessageTypeCarriesASeedLikeMember()
    {
        Assembly assembly = typeof(IProtocolMessage).Assembly;
        Type[] roots = assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IProtocolMessage).IsAssignableFrom(type))
            .ToArray();
        Assert.NotEmpty(roots);
        Assert.Contains(typeof(JoinAcceptedMessage), roots);

        HashSet<Type> visited = [];
        Queue<Type> pending = new(roots);
        List<string> offenders = [];
        while (pending.Count > 0)
        {
            Type type = pending.Dequeue();
            if (!visited.Add(type))
            {
                continue;
            }

            const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            IEnumerable<(string Name, Type MemberType)> members =
                type.GetProperties(Members).Select(property => (property.Name, property.PropertyType))
                    .Concat(type.GetFields(Members).Select(field => (field.Name, field.FieldType)));
            foreach ((string name, Type memberType) in members)
            {
                if (name.Contains("seed", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{type.FullName}.{name}");
                }

                foreach (Type reachable in ExpandTypes(memberType))
                {
                    if (reachable.Assembly == assembly && !visited.Contains(reachable))
                    {
                        pending.Enqueue(reachable);
                    }
                }
            }
        }

        Assert.Empty(offenders);
        // The walk must reach nested payload types, not just the top-level messages.
        Assert.True(visited.Count > roots.Length);
    }

    [Fact]
    public void ProtocolSourceFilesDoNotMentionSimulationSeed()
    {
        string protocolDirectory = Path.Combine(FindRepoRoot(), "dream", "Multiplayer", "Protocol");
        string[] files = Directory.GetFiles(protocolDirectory, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        string[] offenders = files
            .Where(file => File.ReadAllText(file).Contains("SimulationSeed", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> ExpandTypes(Type type)
    {
        if (type.HasElementType && type.GetElementType() is Type element)
        {
            foreach (Type nested in ExpandTypes(element))
            {
                yield return nested;
            }

            yield break;
        }

        if (Nullable.GetUnderlyingType(type) is Type underlying)
        {
            yield return underlying;
            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type nested in ExpandTypes(argument))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class TempWorld : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly string _schemaPath;

        public TempWorld()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-world-seed-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            _schemaPath = Path.Combine(FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
            DatabasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
        }

        public string DatabasePath { get; }

        public ServerPersistenceBundle OpenBundle(string? simulationSeedOverride = null, Action<string>? log = null)
        {
            return ServerBootstrap.CreatePersistenceBundle(DatabasePath, _schemaPath, simulationSeedOverride, log);
        }

        // A database as the server wrote it before world_metadata existed: the shipped init
        // script only (the table is created by EnsureSchema, not by the script).
        public void CreateLegacyDatabaseWithoutMetadataTable()
        {
            Execute(File.ReadAllText(_schemaPath));
        }

        public void Execute(string sql)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public bool TableExists(string tableName)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }

        public void Dispose()
        {
            SqlitePools.Release(DatabasePath);
            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private SqliteConnection Open()
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
            connection.Open();
            return connection;
        }
    }
}
