using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server;

// Persistence wave E (B): a stored world is hosted from its terrain_chunks rows.
public sealed class StoredWorldHostingTests
{
    public enum Damage { Missing, Partial, Corrupt, OverLimit, CorruptBytes }

    [Fact]
    public void Bootstrap_OpensAStoredWorld_AndItsRuntimeChecksumEqualsTheGeneratedSources()
    {
        using StoredWorldFixture fixture = new();
        List<string> log = [];

        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(log.Add), null);
        using RuntimeAuthoritativeSessionHost generated = ServerBootstrap.CreateRuntimeHost(null, null);

        Assert.Equal(WorldMapSource.Stored, host.Runtime.World.MapSource);
        Assert.Equal(generated.Runtime.Map.WorldChecksum, host.Runtime.Map.WorldChecksum);
        Assert.Contains(log, line => line.StartsWith("World 'stone' (id 2) stored terrain: chunks=", StringComparison.Ordinal)
            && line.Contains(" bytes=", StringComparison.Ordinal) && line.Contains(" sha256=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Damage.Missing)]
    [InlineData(Damage.Partial)]
    [InlineData(Damage.Corrupt)]
    [InlineData(Damage.OverLimit)]
    [InlineData(Damage.CorruptBytes)]
    public void DamagedTerrain_ExitsWithCode5_AndALog(Damage damage)
    {
        using StoredWorldFixture fixture = new();
        fixture.Damage(damage);
        List<string> log = [];

        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(fixture.OpenBundle(log.Add)),
            log.Add,
            out int exitCode,
            "stone");

        Assert.Null(host);
        Assert.Equal(ServerBootstrap.WorldCannotBeOpenedExitCode, exitCode);
        Assert.Contains(log, line => line.Contains("Server not started", StringComparison.Ordinal)
            && line.Contains("stored terrain", StringComparison.Ordinal));
    }

    [Fact]
    public void StoredJoins_CarryTheManifest_AndShareOnePayloadInstance()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);

        JoinAcceptedMessage first = host.JoinSession("a", "Alpha");
        JoinAcceptedMessage second = host.JoinSession("b", "Beta");
        JoinAcceptedMessage again = host.JoinSession("a", "Alpha");

        Assert.NotNull(first.TerrainPayload);
        Assert.Equal(first.TerrainPayload!.Manifest, first.Terrain);
        Assert.Equal("stored", first.World!.MapSource);
        Assert.Same(first.TerrainPayload, second.TerrainPayload);
        Assert.Same(first.TerrainPayload, again.TerrainPayload);
        Assert.Equal(first.Terrain, again.Terrain);
    }

    [Fact]
    public void GeneratedJoins_HaveNoTerrain()
    {
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(null, null);

        JoinAcceptedMessage first = host.JoinSession("a", "Alpha");
        JoinAcceptedMessage again = host.JoinSession("a", "Alpha");

        Assert.Null(first.Terrain);
        Assert.Null(first.TerrainPayload);
        Assert.Null(again.Terrain);
        Assert.Null(again.TerrainPayload);
    }
}

// A migrated temp database with a stored 56x56 world 'stone' (id 2) whose chunks are exported
// from the default generated map.
internal sealed class StoredWorldFixture : IDisposable
{
    private readonly MigrationTestDatabase _database = new();

    public StoredWorldFixture()
    {
        _database.Schema.EnsureSchema();
        _database.ExecuteScript(
            "INSERT INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version, seed, world_time_ticks, created_schema_version) " +
            "VALUES (2, 'stone', 56, 8, 56, 'stored', 'default', 1, '1', 0, 6);");
        MapDefinition definition = new(56, 8, 56, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        Chunks = TerrainChunkExporter.Export(new GeneratedMapSource(definition));
        new SqliteTerrainChunkRepository(new SqliteWorldStore(_database.Schema), new WorldId(2)).ReplaceTerrainChunks(Chunks);
    }

    public IReadOnlyList<PersistedTerrainChunk> Chunks { get; }

    // For SqlitePools.Release: this fixture's own database, never the whole process's pools.
    public string DatabasePath => _database.DatabasePath;

    public ServerPersistenceBundle OpenBundle(Action<string>? log = null) =>
        ServerBootstrap.CreatePersistenceBundle(
            _database.DatabasePath,
            MigrationTestDatabase.InitScriptPath,
            log: log,
            start: new ServerStartOptions(WorldSelector: "stone"));

    public void Damage(StoredWorldHostingTests.Damage damage)
    {
        switch (damage)
        {
            case StoredWorldHostingTests.Damage.Missing:
                _database.ExecuteScript("DELETE FROM terrain_chunks WHERE world_id = 2;");
                break;
            case StoredWorldHostingTests.Damage.Partial:
                _database.ExecuteScript("DELETE FROM terrain_chunks WHERE world_id = 2 AND chunk_x = 0 AND chunk_z = 0;");
                break;
            case StoredWorldHostingTests.Damage.Corrupt:
                _database.ExecuteScript("UPDATE terrain_chunks SET format_version = 99 WHERE world_id = 2 AND chunk_x = 0 AND chunk_z = 0;");
                break;
            case StoredWorldHostingTests.Damage.CorruptBytes:
                CorruptOneBitmapByte();
                break;
            default:
                _database.ExecuteScript(
                    $"UPDATE terrain_chunks SET data = zeroblob({WorldTransferLimits.Default.MaxTerrainBlobBytes + 1}) " +
                    "WHERE world_id = 2 AND chunk_x = 0 AND chunk_z = 0;");
                break;
        }
    }

    // A blob whose header, format version, size and row cell count are all valid, but whose occupancy
    // bitmap has one byte cleared (every column holds the floor, so the byte was not zero): only a full
    // decode notices.
    private void CorruptOneBitmapByte()
    {
        PersistedTerrainChunk[] chunks = [.. Chunks];
        byte[] data = (byte[])chunks[0].Data.Clone();
        const int headerSize = 23;
        int paletteCount = data[headerSize - 1];
        int bitmapByte = headerSize + (4 * paletteCount) + 5;
        Assert.NotEqual(0, data[bitmapByte]);
        data[bitmapByte] = 0;
        chunks[0] = chunks[0] with { Data = data };
        new SqliteTerrainChunkRepository(new SqliteWorldStore(_database.Schema), new WorldId(2)).ReplaceTerrainChunks(chunks);
    }

    public void Dispose()
    {
        SqlitePools.Release(_database.DatabasePath);
        _database.Dispose();
    }
}
