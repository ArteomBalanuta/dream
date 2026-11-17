using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Server.Persistence.Migrations;
using static Dream.Tests.Integration.ObjectIdTestSupport;

namespace Dream.Tests.Integration;

// Persistence wave E, workstream D: test support for hosting a stored world (stored chunks as the
// map source) on the C0 plumbing alone (WorldSessionRuntime(... storedTerrain), RuntimePersistencePorts
// .StoredTerrain), so these tests do not depend on ServerBootstrap hosting stored worlds (workstream B).
//
// The database holds world 1 only: its size is set, its generated terrain is exported to terrain_chunks
// through TerrainChunkExporter and SqliteTerrainChunkRepository.ReplaceTerrainChunks, and its
// map_source flips to 'stored'. Every ObjectIdTestSupport helper (ReadIds, DumpWorld, Scalar) reads
// world 1, so they apply unchanged.
internal sealed class StoredWorldDb : IDisposable
{
    public const ulong DefaultSeed = 20261029UL;

    private readonly MigrationTestDatabase _database;

    public StoredWorldDb(int size = WorldDefaults.StartupFloorSize, bool stored = true, ulong seed = DefaultSeed)
    {
        _database = new MigrationTestDatabase();
        _database.Schema.EnsureSchema();
        Size = size;
        Definition = new MapDefinition(size, 8, size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        BaseChunks = stored ? TerrainChunkExporter.Export(new GeneratedMapSource(Definition)) : [];
        string mapSource = WorldMapSourceText.ToStorage(stored ? WorldMapSource.Stored : WorldMapSource.Generated);
        _database.ExecuteScript(
            $"UPDATE worlds SET width = {size}, depth = {size}, height = 8, map_source = '{mapSource}', " +
            $"seed = '{seed.ToString(CultureInfo.InvariantCulture)}' WHERE world_id = 1;");
        if (stored)
        {
            new SqliteTerrainChunkRepository(new SqliteWorldStore(_database.Schema), WorldId.Default).ReplaceTerrainChunks(BaseChunks);
        }

        IsStored = stored;
        SqlitePools.Release(_database.DatabasePath);
    }

    private StoredWorldDb(StoredWorldDb source)
    {
        // The SOURCE's pool, so its file (and its -wal) is closed before the byte copy below.
        SqlitePools.Release(source.DatabasePath);
        _database = new MigrationTestDatabase();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(source.DatabasePath + suffix))
            {
                File.Copy(source.DatabasePath + suffix, _database.DatabasePath + suffix);
            }
        }

        Size = source.Size;
        Definition = source.Definition;
        BaseChunks = source.BaseChunks;
        IsStored = source.IsStored;
    }

    public int Size { get; }

    public bool IsStored { get; }

    // The generated definition the stored terrain was exported from.
    public MapDefinition Definition { get; }

    // The terrain as exported before any edit (empty for a generated world).
    public IReadOnlyList<PersistedTerrainChunk> BaseChunks { get; }

    public string DatabasePath => _database.DatabasePath;

    public SqliteSchemaBootstrapper Schema => _database.Schema;

    public MigrationTestDatabase Database => _database;

    // A byte copy of the database file, for lockstep pairs. Call after the servers are disposed.
    public StoredWorldDb Copy() => new(this);

    public WorldRecord Record()
    {
        _database.Schema.EnsureSchema();
        return new SqliteWorldCatalog(_database.Schema).TryGet(WorldId.Default)
            ?? throw new InvalidOperationException("world 1 is missing");
    }

    // The bundle and the stored terrain (null for a generated world) as the bootstrap would hand them to
    // the runtime.
    public (ServerPersistenceBundle Bundle, IReadOnlyList<PersistedTerrainChunk>? Terrain) OpenBundle()
    {
        WorldRecord record = Record();
        ServerPersistenceBundle bundle = WorldBundles.Bind(_database.Schema, record);
        IReadOnlyList<PersistedTerrainChunk>? terrain = record.Descriptor.MapSource == WorldMapSource.Stored
            ? new SqliteTerrainChunkRepository(new SqliteWorldStore(_database.Schema), WorldId.Default).LoadTerrainChunks()
            : null;
        return (bundle, terrain);
    }

    public DedicatedServerHost CreateHost()
    {
        (ServerPersistenceBundle bundle, IReadOnlyList<PersistedTerrainChunk>? terrain) = OpenBundle();
        return CreateHost(bundle, terrain);
    }

    // The host ServerBootstrap.CreateAuthoritativeHost builds (deferred joins, persistence ports, the
    // world clock), with the stored terrain handed to the runtime through the ports.
    public static DedicatedServerHost CreateHost(ServerPersistenceBundle bundle, IReadOnlyList<PersistedTerrainChunk>? terrain)
    {
        RuntimePersistencePorts ports = ServerBootstrap.CreateRuntimePersistencePorts(bundle)! with { StoredTerrain = terrain };
        RuntimeAuthoritativeSessionHost authority = RuntimeAuthoritativeSessionHost.CreateDefault(ports);
        authority.Runtime.ConfigureWorldClock(
            WorldClockSettings.FromDayLengthMinutes(
                ServerBootstrap.ResolveDayLengthMinutes(Environment.GetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable))),
            bundle.WorldTimeTicks);
        ProtocolRouter router = new(
            [new ClientHelloHandler(), new JoinRequestHandler(), new PlayerCommandHandler(), new PlayerActionHandler(), new WorldResyncRequestHandler()],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(authority, router, deferJoinsToTick: true);
    }

    public void Dispose()
    {
        _database.Dispose();
    }
}

// What a stored world must satisfy after every restart (the soak's checks, also used by the edit tests).
internal static class StoredWorldChecks
{
    // Restored map against the database: the override rows are exactly the removed source cells, the
    // rubble in the map is the rubble in the database (one group per parent cell), ids are unique and
    // below the counter, the map holds no invariant violation, and the file passes integrity_check.
    // Call with the server disposed (SqlitePools.Release(db.DatabasePath) first).
    public static string AssertConsistent(StoredWorldDb db, Map map, long minimumNextObjectId = 0)
    {
        SqlitePools.Release(db.DatabasePath);

        // 1. Overrides == removed base cells.
        HashSet<GeometryCellKey> baseCells = [.. new StoredMapSource(db.Definition, db.BaseChunks).CreateCells()
            .Where(cell => cell.Key.Y > 0).Select(cell => cell.Key)];
        HashSet<GeometryCellKey> removed = [.. baseCells.Where(key => !map.IsSourceCell(key))];
        HashSet<GeometryCellKey> overrides = ReadOverrideCells(db.DatabasePath);
        Assert.True(
            overrides.SetEquals(removed),
            $"override rows ({overrides.Count}) differ from the removed source cells ({removed.Count}): " +
            $"only rows {string.Join(",", overrides.Except(removed).Take(5))}; only removed {string.Join(",", removed.Except(overrides).Take(5))}");

        // 2. No orphan rubble: one origin per parent cell, and the map holds exactly those cells.
        Dictionary<long, (long Origin, long X, long Y, long Z)> pieces = ReadPieceGroups(db.DatabasePath);
        foreach (var cellGroup in pieces.Values.GroupBy(piece => (piece.X, piece.Y, piece.Z)))
        {
            Assert.True(
                cellGroup.Select(piece => piece.Origin).Distinct().Count() == 1,
                $"cell {cellGroup.Key} holds rubble from more than one group");
        }

        HashSet<GeometryCellKey> rubbleCells = [.. pieces.Values.Select(piece => new GeometryCellKey((int)piece.X, (int)piece.Y, (int)piece.Z))];
        Assert.True(
            rubbleCells.SetEquals(map.RubbleCells.Keys),
            $"rubble cells in the database ({rubbleCells.Count}) differ from the map ({map.RubbleCells.Count})");

        // 3. Ids.
        PersistedIds ids = ReadIds(db.DatabasePath);
        Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
        Assert.Empty(ids.Origins.Intersect(ids.AllObjects));
        Assert.Empty(ids.Overrides.Intersect(ids.AllObjects));
        Assert.Equal(ids.Overrides.Count, ids.Overrides.Distinct().Count());
        Assert.True(ids.NextObjectId > ids.MaxId, $"counter {ids.NextObjectId} is not above the highest id {ids.MaxId}");
        Assert.True(ids.NextObjectId >= minimumNextObjectId, $"counter {ids.NextObjectId} went below {minimumNextObjectId}");

        // 4. The map and the file.
        Assert.Empty(map.FindInvariantViolations());
        using (SqliteConnection connection = Open(db.DatabasePath))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }

        // The stored chunks are never rewritten by edits.
        Assert.Equal(db.BaseChunks.Count, (int)Scalar(db.DatabasePath, "SELECT COUNT(*) FROM terrain_chunks WHERE world_id = 1;"));
        return $"overrides {overrides.Count}, rubble cells {rubbleCells.Count}, counter {ids.NextObjectId}";
    }

    public static HashSet<GeometryCellKey> ReadOverrideCells(string databasePath)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT cell_x, cell_y, cell_z FROM terrain_cell_overrides WHERE world_id = 1 AND kind = 0;";
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<GeometryCellKey> cells = [];
        while (reader.Read())
        {
            cells.Add(new GeometryCellKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return cells;
    }

    // Number of diagnostics lines of one category while the action runs on this thread.
    public static List<(string Category, string Message)> CaptureLog(Action action)
    {
        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            action();
        }

        return lines;
    }
}

// One restart-able scripted server for the stored-world soak: two joined sessions, one of which
// (session-a) fights and builds. Every 64 ticks the primary picks a plan from the map alone, so two
// servers on equal state do the same: attack the nearest standing cube in reach (terrain or a placed
// cube), or, with none in reach, place a cube in a free cell beside it (rubble cells first, which
// are the removed cells). Reads no clock and no random number.
internal sealed class SoakServer : IDisposable
{
    public const string Primary = "session-a";
    public const string Secondary = "session-b";
    private readonly int _slotTicks;
    private const float Reach = 3.2f;
    private const float BuildReach = 2.5f;

    private readonly DedicatedServerHost _host;
    private readonly List<PendingConnectionReply> _joinReplies = [];
    private ulong _commandSequence;
    private Vector3 _aim = Vector3.UnitX;
    private bool _attacking;
    private bool _walking;
    private int _slot;
    private int _blocked;
    private Vector3? _walkedFrom;

    public SoakServer(DedicatedServerHost host, int slotTicks = 64, bool clearEnemies = true)
    {
        _host = host;
        _slotTicks = slotTicks;
        Runtime = host.Runtime!;
        foreach (string session in new[] { Primary, Secondary })
        {
            Assert.Empty(_host.HandleClientMessage($"connection-{session}", new ClientJoinRequestMessage(session, $"Name {session}", Dream.Tests.Support.TestAccountTokens.For($"Name {session}"))));
        }

        Assert.True(Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        Assert.Equal(1, AdvanceOneTick());
        _host.DrainPendingJoinRepliesInto(_joinReplies);
        Assert.Equal(2, _joinReplies.Count);
        Assert.All(_joinReplies, reply => Assert.IsType<JoinAcceptedMessage>(reply.Message));
        _host.DrainPendingActionResults();
        _ = _host.CreateSnapshotMessage();
        if (clearEnemies)
        {
            Runtime.Enemies.Clear();
        }
    }

    public WorldSessionRuntime Runtime { get; }

    public DedicatedServerHost Host => _host;

    public int Fractures { get; private set; }

    public int Placements { get; private set; }

    public Dictionary<string, int> Rejections { get; } = [];

    public ulong Hash() => Dream.Simulation.Determinism.SimulationStateChecksum.Compute(Runtime);

    // One server tick of the script. localTick starts at 0 and counts ticks run by this server.
    public void Step(int localTick)
    {
        bool slotStart = localTick % _slotTicks == 0;
        PlayerInteractionMode mode = PlayerInteractionMode.Combat;
        BuildUseIntent? build = null;
        if (slotStart)
        {
            Plan(out build, out bool attack, out bool walk);
            _attacking = attack;
            _walking = walk;
            if (build != null)
            {
                mode = PlayerInteractionMode.Building;
            }
        }

        foreach (string session in new[] { Primary, Secondary })
        {
            bool primary = session == Primary;
            _commandSequence++;
            _host.HandleClientMessage(
                $"connection-{session}",
                new PlayerCommandMessage(new PlayerCommandFrame(
                    new PlayerSessionId(session),
                    _commandSequence,
                    new PlayerModeSwitchIntent(primary ? mode : PlayerInteractionMode.Building),
                    primary && _walking ? new PlayerMoveIntent(1f, 0f) : PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, primary ? _aim : Vector3.UnitZ),
                    PrimaryUsePressed: primary && _attacking && build == null)));
        }

        if (build != null)
        {
            // The session's action sequence is shared with the uses its command frames synthesize, so a
            // build must come after every command sequence sent so far.
            _commandSequence++;
            Assert.True(_host.TryEnqueueServerAction(new PlayerActionMessage(new PlayerSessionId(Primary), _commandSequence, build)));
        }

        Assert.Equal(1, AdvanceOneTick());
        foreach (AuthoritativeActionResult result in _host.DrainPendingActionResults())
        {
            if (result is { Accepted: true, Payload: WorldMutationResult { Kind: WorldMutationKind.CubePlaced, Applied: true } })
            {
                Placements++;
            }
            else if (result is { Accepted: false, Rejection: { } rejection })
            {
                Rejections[rejection.Reason] = Rejections.GetValueOrDefault(rejection.Reason) + 1;
            }
        }

        ServerSnapshotMessage snapshot = _host.CreateSnapshotMessage();
        foreach (WorldMutationDelta mutation in snapshot.Snapshot.WorldMutations)
        {
            if (mutation.Kind == WorldMutationKind.CubeFractured)
            {
                Fractures++;
            }
        }
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private void Plan(out BuildUseIntent? build, out bool attack, out bool walk)
    {
        build = null;
        attack = false;
        walk = false;
        Map map = Runtime.Map;
        ActorSnapshot actor = Runtime.CaptureActorSnapshots().Single(candidate => candidate.SessionId == Primary);
        Vector3 origin = actor.Position;
        Vector3 eye = origin + new Vector3(0f, 0.35f, 0f);
        int slot = _slot++;

        // Every fourth slot places a cube beside the player (rubble cells, the removed ones, first).
        if (slot % 4 == 3 && TryFindFreeCell(map, origin, out GeometryCellKey freeCell))
        {
            build = new BuildUseIntent(new Vector3(freeCell.X, freeCell.Y, freeCell.Z));
            _aim = Aim(new Vector3(freeCell.X + 0.5f, freeCell.Y + 0.5f, freeCell.Z + 0.5f) - eye);
            return;
        }

        GeometryCellKey? target = null;
        float best = float.MaxValue;
        int radius = (int)MathF.Ceiling(Reach);
        for (int x = Math.Max(0, (int)eye.X - radius); x <= Math.Min(map.Width - 1, (int)eye.X + radius); x++)
        {
            for (int z = Math.Max(0, (int)eye.Z - radius); z <= Math.Min(map.Depth - 1, (int)eye.Z + radius); z++)
            {
                for (int y = 1; y < map.Height; y++)
                {
                    if (map.Cubes[x, y, z] == null)
                    {
                        continue;
                    }

                    float distance = (new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - eye).LengthSquared;
                    if (distance <= Reach * Reach && distance < best)
                    {
                        best = distance;
                        target = new GeometryCellKey(x, y, z);
                    }
                }
            }
        }

        if (target is { } cell)
        {
            _aim = Aim(new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f) - eye);
            attack = true;
            return;
        }

        // Nothing in reach: walk toward a standing cube above the floor, the nearest one, or the next
        // nearest each time the previous walk did not get anywhere (blocked).
        List<(float Distance, Vector3 Delta)> goals = [];
        foreach (Cube cube in map.SolidCubes)
        {
            if (cube.Position.Y <= 1f)
            {
                continue;
            }

            Vector3 delta = cube.Position - origin;
            delta.Y = 0f;
            goals.Add((delta.LengthSquared, delta));
        }

        if (goals.Count > 0)
        {
            if (_walkedFrom is { } previous && (previous - origin).Length < 0.5f)
            {
                _blocked++;
            }
            else
            {
                _blocked = 0;
            }

            _walkedFrom = origin;
            goals.Sort((left, right) => left.Distance.CompareTo(right.Distance));
            _aim = Aim(goals[_blocked % goals.Count].Delta);
            walk = true;
            return;
        }

        if (TryFindFreeCell(map, origin, out GeometryCellKey fallback))
        {
            build = new BuildUseIntent(new Vector3(fallback.X, fallback.Y, fallback.Z));
            _aim = Aim(new Vector3(fallback.X + 0.5f, fallback.Y + 0.5f, fallback.Z + 0.5f) - eye);
        }
    }

    private static Vector3 Aim(Vector3 direction) =>
        direction.LengthSquared < 0.0001f ? Vector3.UnitX : direction.Normalized();

    private static bool TryFindFreeCell(Map map, Vector3 origin, out GeometryCellKey cell)
    {
        GeometryCellKey? free = null;
        (int Rank, float Distance) bestFree = (int.MaxValue, float.MaxValue);
        for (int x = Math.Max(0, (int)origin.X - 3); x <= Math.Min(map.Width - 1, (int)origin.X + 3); x++)
        {
            for (int z = Math.Max(0, (int)origin.Z - 3); z <= Math.Min(map.Depth - 1, (int)origin.Z + 3); z++)
            {
                GeometryCellKey candidate = new(x, 1, z);
                if (map.Cubes[x, 1, z] != null || map.GeometryCells.ContainsKey(candidate))
                {
                    continue;
                }

                float distance = (new Vector3(x, 1f, z) - origin).Length;
                if (distance > BuildReach || distance < 1.1f)
                {
                    continue;
                }

                (int Rank, float Distance) key = (map.RubbleCells.ContainsKey(candidate) ? 0 : 1, distance);
                if (key.CompareTo(bestFree) < 0)
                {
                    bestFree = key;
                    free = candidate;
                }
            }
        }

        cell = free ?? default;
        return free.HasValue;
    }

    private int AdvanceOneTick()
    {
        int executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        return executed;
    }
}
