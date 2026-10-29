using System.Globalization;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Worlds;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Server;

// Persistence wave B2: the server resolves its world through IWorldCatalog and builds its
// persistence bundle and runtime ports from the result. Most tests use a fake catalog; the RealCatalog tests
// use the SQLite catalog on a temp database.
public sealed class WorldSelectionBootstrapTests : IDisposable
{
    private const ulong RecordSeed = 0x1234_5678_9ABC_DEF0UL;

    private readonly string _rootDirectory;
    private readonly string _schemaPath;
    private readonly string _databasePath;

    public WorldSelectionBootstrapTests()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-world-select-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);
        _schemaPath = Path.Combine(FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
        _databasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ResolveWorldSelector_PrefersTheArgumentOverTheEnvironment()
    {
        Assert.Equal(new WorldId(3), ServerBootstrap.ResolveWorldSelector("3", "other").Id);
        Assert.Equal("other", ServerBootstrap.ResolveWorldSelector(null, "other").Name);
        Assert.Equal("other", ServerBootstrap.ResolveWorldSelector("  ", "other").Name);
        Assert.True(ServerBootstrap.ResolveWorldSelector(null, null).IsDefault);
        Assert.True(ServerBootstrap.ResolveWorldSelector("", "").IsDefault);
    }

    [Fact]
    public void ResolveWorldSelector_InvalidId_IsWorldNotFound()
    {
        WorldNotFoundException ex = Assert.Throws<WorldNotFoundException>(
            () => ServerBootstrap.ResolveWorldSelector("0", null));
        Assert.Equal("0", ex.Selector);
    }

    [Fact]
    public void Bundle_PassesSelectorAndNewWorldSizeToTheCatalog()
    {
        FakeCatalog catalog = new(Record(WorldDescriptor.Default));

        using ServerPersistenceBundle bundle = Open(catalog, new ServerStartOptions("alpha", 20));

        Assert.Equal("alpha", catalog.LastSelector.Name);
        Assert.Null(catalog.LastSelector.Id);
        Assert.Equal(20, catalog.LastNewWorldSize);
    }

    [Fact]
    public void Bundle_IdSelector_ReachesTheCatalogAsAnId()
    {
        FakeCatalog catalog = new(Record(WorldDescriptor.Default));

        using ServerPersistenceBundle bundle = Open(catalog, new ServerStartOptions("1"));

        Assert.Equal(new WorldId(1), catalog.LastSelector.Id);
        Assert.Null(catalog.LastSelector.Name);
    }

    [Fact]
    public void Bundle_WithoutOptions_UsesTheDefaultSelectorAndStartupSize()
    {
        FakeCatalog catalog = new(Record(WorldDescriptor.Default));

        using ServerPersistenceBundle bundle = Open(catalog, start: null);

        Assert.True(catalog.LastSelector.IsDefault);
        Assert.Equal(WorldDefaults.StartupFloorSize, catalog.LastNewWorldSize);
    }

    [Fact]
    public void Bundle_TakesDescriptorSeedAndTimeFromTheResolvedWorld()
    {
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, WorldId.Default, "small");
        FakeCatalog catalog = new(Record(small, seed: RecordSeed, time: 4321));

        using ServerPersistenceBundle bundle = Open(catalog, new ServerStartOptions("small"));

        Assert.Equal(small, bundle.Descriptor);
        Assert.Equal(WorldId.Default, bundle.WorldId);
        Assert.Equal(RecordSeed, bundle.SimulationSeed);
        Assert.Equal(4321, bundle.WorldTimeTicks);
    }

    [Fact]
    public void Bundle_SeedOverrideWinsOverTheWorldSeed()
    {
        FakeCatalog catalog = new(Record(WorldDescriptor.Default, seed: RecordSeed));

        using ServerPersistenceBundle bundle = Open(catalog, null, simulationSeedOverride: "77");

        Assert.Equal(77UL, bundle.SimulationSeed);
    }

    [Fact]
    public void Bundle_Log_NamesTheWorldAndNeverTheSeed()
    {
        List<string> lines = [];
        FakeCatalog catalog = new(
            Record(WorldDescriptor.ForFloorSize(20, WorldId.Default, "small"), seed: RecordSeed), created: true);

        using ServerPersistenceBundle bundle = Open(catalog, new ServerStartOptions("small", 20), lines.Add);

        string log = string.Join("\n", lines);
        Assert.Contains("small", log);
        Assert.Contains("id 1", log);
        Assert.Contains("created", log);
        Assert.DoesNotContain(RecordSeed.ToString(CultureInfo.InvariantCulture), log);
        Assert.DoesNotContain(RecordSeed.ToString("X", CultureInfo.InvariantCulture), log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bundle_WorldNotFound_PropagatesFromTheCatalog()
    {
        FakeCatalog catalog = new(null) { Throw = new WorldNotFoundException("9") };

        Assert.Throws<WorldNotFoundException>(() => Open(catalog, new ServerStartOptions("9")));
    }

    [Fact]
    public void TryCreateServerModeHost_WorldNotFound_ExitsWithCode4()
    {
        List<string> lines = [];

        DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => throw new WorldNotFoundException("nope"),
            lines.Add,
            out int exitCode);

        Assert.Null(host);
        Assert.Equal(4, exitCode);
        Assert.Equal(ServerBootstrap.WorldNotFoundExitCode, exitCode);
        Assert.Contains(lines, line => line.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public void Bundle_InvalidResolvedDescriptor_IsRejected()
    {
        WorldDescriptor tooBig = WorldDescriptor.ForFloorSize(500);
        FakeCatalog catalog = new(Record(tooBig));

        Assert.Throws<ArgumentException>(() => Open(catalog, null));
    }

    [Fact]
    public void RuntimePorts_CarryTheBundlesWorldDescriptor()
    {
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, WorldId.Default, "small");
        FakeCatalog catalog = new(Record(small, seed: RecordSeed));
        using ServerPersistenceBundle bundle = Open(catalog, null);

        RuntimePersistencePorts? ports = ServerBootstrap.CreateRuntimePersistencePorts(bundle);

        Assert.NotNull(ports);
        Assert.Equal(small, ports!.Descriptor);
    }

    [Fact]
    public void RuntimeHost_FromABundleOfASmallWorld_BuildsASmallMap()
    {
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, WorldId.Default, "small");
        FakeCatalog catalog = new(Record(small, seed: RecordSeed, time: 100));
        ServerPersistenceBundle bundle = Open(catalog, null);

        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(bundle, null);

        Assert.Equal(small, host.Runtime.World);
        Assert.Equal(20, host.Runtime.Map.Width);
        Assert.Equal(RecordSeed, host.Runtime.SimulationSeed);
        Assert.Equal(100, host.Runtime.WorldClock.WorldTicks);
    }

    [Fact]
    public void RealCatalog_DefaultSelectorOpensWorldOne()
    {
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            _databasePath, _schemaPath, start: new ServerStartOptions(null));

        Assert.Equal(WorldId.Default, bundle.WorldId);
        Assert.Equal("default", bundle.Descriptor!.Name);
        Assert.NotNull(bundle.SimulationSeed);
    }

    [Fact]
    public void RealCatalog_NamedSelectorCreatesTheWorld()
    {
        List<string> lines = [];

        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            _databasePath, _schemaPath, log: lines.Add, start: new ServerStartOptions("foo", 20));

        Assert.Equal("foo", bundle.Descriptor!.Name);
        Assert.Equal(20, bundle.Descriptor.Width);
        Assert.NotEqual(WorldId.Default, bundle.WorldId);
        Assert.Contains(lines, line => line.Contains("created", StringComparison.Ordinal));
    }

    [Fact]
    public void RealCatalog_UnknownIdExitsWithCode4()
    {
        List<string> lines = [];

        DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () =>
            {
                ServerBootstrap.CreatePersistenceBundle(
                    _databasePath, _schemaPath, start: new ServerStartOptions("999")).Dispose();
                throw new InvalidOperationException("unreachable");
            },
            lines.Add,
            out int exitCode);

        Assert.Null(host);
        Assert.Equal(4, exitCode);
    }

    [Fact]
    public void Bundle_ForWorldTwo_BindsRepositoriesAndPortsToWorldTwo()
    {
        WorldDescriptor second = WorldDescriptor.ForFloorSize(20, new WorldId(2), "second");
        FakeCatalog catalog = new(Record(second, seed: RecordSeed));

        using ServerPersistenceBundle bundle = Open(catalog, new ServerStartOptions("second"));

        Assert.Equal(new WorldId(2), bundle.WorldId);
        Assert.Equal(second, bundle.Descriptor);
        Assert.Equal(second, ServerBootstrap.CreateRuntimePersistencePorts(bundle)!.Descriptor);
    }

    private ServerPersistenceBundle Open(
        IWorldCatalog catalog,
        ServerStartOptions? start,
        Action<string>? log = null,
        string? simulationSeedOverride = null)
    {
        return ServerBootstrap.CreatePersistenceBundle(
            _databasePath, _schemaPath, simulationSeedOverride, log, start, catalog);
    }

    private static WorldRecord Record(WorldDescriptor descriptor, ulong? seed = null, long time = 0)
    {
        DateTime now = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        return new WorldRecord(descriptor, seed, time, 3, now, now);
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

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FakeCatalog(WorldRecord? record, bool created = false) : IWorldCatalog
    {
        public Exception? Throw { get; init; }

        public WorldSelector LastSelector { get; private set; }

        public int LastNewWorldSize { get; private set; }

        public WorldRecord Resolve(WorldSelector selector, int newWorldSize, out bool wasCreated)
        {
            LastSelector = selector;
            LastNewWorldSize = newWorldSize;
            wasCreated = created;
            if (Throw != null)
            {
                throw Throw;
            }

            return record!;
        }

        public IReadOnlyList<WorldRecord> List() => throw new NotSupportedException();

        public WorldRecord? TryGet(WorldId id) => throw new NotSupportedException();

        public WorldRecord? TryGetByName(string name) => throw new NotSupportedException();

        public WorldRecord Create(WorldCreateOptions options) => throw new NotSupportedException();

        public WorldRecord Copy(WorldId source, string newName) => throw new NotSupportedException();

        public bool Delete(WorldId id) => throw new NotSupportedException();
    }
}
