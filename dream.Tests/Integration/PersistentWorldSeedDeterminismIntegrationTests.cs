using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// NET-8 with NET-9 and the determinism checksum, end to end on a persistent world: the production
// persistent host (ServerBootstrap.CreatePersistenceBundle on a temp SQLite file, then
// ServerBootstrap.CreateAuthoritativeHost) runs a scripted session with combat, a built cube that
// the primary keeps hitting, stray hits that fracture generated terrain, a persistence checkpoint
// and per-tick SimulationStateChecksum. A restart from the same database rolls with the same
// stored seed and replays the same script identically; a second database gets its own seed and
// diverges at a damage roll. The seed never reaches the wire.
//
// The restart replay is here; the divergence, wire and placed-cube damage cases live in
// PersistentWorldSeedDivergenceIntegrationTests, PersistentWorldSeedLoopbackIntegrationTests and
// PersistentWorldPlacedCubeDamageIntegrationTests so the long scripted runs go in parallel. They
// share the scripted server and helpers below.
public class PersistentWorldSeedDeterminismIntegrationTests(ITestOutputHelper output)
{
    // Past the first 5 s persistence checkpoint (320 ticks), so the run also covers a checkpoint.
    internal const int Ticks = 400;
    internal const int EnemyActorId = 4300;
    internal const int EnemyMaxHealth = 400;
    internal const string PrimarySession = "session-a";
    internal const string SessionB = "session-b";
    internal const string SessionC = "session-c";
    internal static readonly string[] Sessions = [PrimarySession, SessionB, SessionC];

    internal const int WalkUntil = 20;
    internal const int BuildAt = 3;
    internal const int FightFrom = 30;
    internal const int CubeAttackFrom = 130;
    // DedicatedServerHost captures a persistence checkpoint every 5 s of server time.
    internal const int CheckpointTicks = 5 * SimulationClock.AuthoritativeTicksPerSecond;

    internal static readonly Vector3 EnemyPosition = new(7.5f, 1f, 11f);

    [Fact]
    public void Restart_FromTheSameDatabase_KeepsTheStoredSeed_AndTheScriptReplaysEveryTickChecksum()
    {
        using TempDatabase database = new();

        ulong seed;
        RunRecord first;
        Vector3 buildCell;
        bool builtCubeBroke;
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            seed = server.Seed;
            buildCell = server.BuildCell;
            Assert.NotEqual(WorldSessionRuntime.DefaultSimulationSeed, seed);
            first = server.RunScript(Script);
            Assert.NotNull(server.BuiltCubeId);
            Assert.NotEmpty(server.BuiltCubeHits);
            builtCubeBroke = server.BuiltCubeFractureTick > 0;
            output.WriteLine($"first run: built cube {server.BuiltCubeId} at {buildCell} broke at {server.BuiltCubeFractureTick}, hits {DescribeHits(server)}");
        }

        AssertScriptExercisedEverything(first);
        SqliteConnection.ClearAllPools();

        // A plain restart: the stored seed comes back, and the built cube comes back as it was
        // left: whole in its cell, or as its persisted fracture pieces. Whether it broke within
        // the script depends on the seed's rolls (PersistentWorld_PlacedCube_KeepsItsDamageBetweenHits
        // pins that damage accumulates between hits). Generated terrain that the
        // secondaries' attacks fracture is not persisted, by design.
        using (ServerPersistenceBundle reopened = database.OpenBundle())
        {
            Assert.Equal(seed, reopened.SimulationSeed);
            Assert.True(new SqliteWorldMetadataRepository(reopened.Schema).TryGetSimulationSeed(out ulong stored));
            Assert.Equal(seed, stored);
            using DedicatedServerHost restarted = ServerBootstrap.CreateAuthoritativeHost(reopened);
            Assert.Equal(seed, restarted.Runtime!.SimulationSeed);
            GeometryCellKey cell = new((int)buildCell.X, (int)buildCell.Y, (int)buildCell.Z);
            if (builtCubeBroke)
            {
                Assert.Equal(0, database.Count("placed_cube_instances"));
                Assert.True(database.Count("cube_fracture_pieces") > 0, "The built cube's fracture pieces were not persisted.");
                Assert.Null(restarted.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
                Assert.Contains(restarted.Runtime.Map.CaptureFracturePieces(), piece => piece.Cell == cell);
            }
            else
            {
                Assert.Equal(1, database.Count("placed_cube_instances"));
                Assert.NotNull(restarted.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
            }
        }

        SqliteConnection.ClearAllPools();

        // Restart again on a fresh world state (every world and player table emptied) but the same
        // seed and clock on its worlds row: the same script must replay with the same checksum every tick.
        database.WipeWorldStateKeepingMetadata();
        RunRecord replay;
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            Assert.Equal(seed, server.Seed);
            replay = server.RunScript(Script);
        }

        Assert.Equal(first.Checksums.Count, replay.Checksums.Count);
        int firstDifference = FirstDifference(first.Checksums, replay.Checksums);
        Assert.True(
            firstDifference < 0,
            $"The replay diverged at tick {firstDifference + 1}: {first.Parts[Math.Max(0, firstDifference)]} vs {replay.Parts[Math.Max(0, firstDifference)]}");
        Assert.Equal(first.HitTicks, replay.HitTicks);
        Assert.Equal(first.EnemyDeathTick, replay.EnemyDeathTick);
        Assert.Equal(first.FractureTick, replay.FractureTick);
        output.WriteLine(
            $"replay: {replay.Checksums.Count} ticks identical; first hit {first.FirstHitTick}, enemy died {first.EnemyDeathTick}, " +
            $"first fracture {first.FractureTick}, roll ticks {first.HitTicks.Count}, rubble {first.RubblePieces}");
    }

    internal static void AssertCubeLosesHealthEveryHitAndBreaks(PersistentScriptedServer server)
    {
        Assert.NotNull(server.BuiltCubeId);
        Assert.True(server.BuiltCubeHits.Count >= 2, $"Only {server.BuiltCubeHits.Count} hits landed on the cube.");
        for (int i = 1; i < server.BuiltCubeHits.Count; i++)
        {
            Assert.True(
                server.BuiltCubeHits[i].Health < server.BuiltCubeHits[i - 1].Health,
                $"The cube's health went {server.BuiltCubeHits[i - 1].Health} -> {server.BuiltCubeHits[i].Health} at tick {server.BuiltCubeHits[i].Tick}: {DescribeHits(server)}");
        }

        Assert.True(server.BuiltCubeFractureTick > 0, $"The cube never broke: {DescribeHits(server)}");
    }

    internal static string DescribeHits(PersistentScriptedServer server)
    {
        return string.Join(", ", server.BuiltCubeHits.Select(hit => $"t{hit.Tick}:{hit.Health}hp"));
    }

    // Decimal, hex (lower and upper, padded and unpadded, with and without 0x), and the raw
    // little- and big-endian bytes.
    internal static byte[][] SeedRepresentations(ulong seed)
    {
        string[] texts =
        [
            seed.ToString(CultureInfo.InvariantCulture),
            seed.ToString("x", CultureInfo.InvariantCulture),
            seed.ToString("X", CultureInfo.InvariantCulture),
            seed.ToString("x16", CultureInfo.InvariantCulture),
            seed.ToString("X16", CultureInfo.InvariantCulture),
            "0x" + seed.ToString("x", CultureInfo.InvariantCulture),
            "0x" + seed.ToString("X", CultureInfo.InvariantCulture),
            Convert.ToBase64String(BitConverter.GetBytes(seed))
        ];
        byte[] little = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(little, seed);
        byte[] big = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(big, seed);
        return [.. texts.Distinct(StringComparer.Ordinal).Select(Encoding.UTF8.GetBytes), little, big];
    }

    internal static string Describe(byte[] needle)
    {
        return needle.All(value => value is >= 0x20 and < 0x7f) ? Encoding.ASCII.GetString(needle) : Convert.ToHexString(needle);
    }

    internal static int FirstDifference(IReadOnlyList<ulong> left, IReadOnlyList<ulong> right)
    {
        for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            if (left[i] != right[i])
            {
                return i;
            }
        }

        return left.Count == right.Count ? -1 : Math.Min(left.Count, right.Count);
    }

    internal static void AssertScriptExercisedEverything(RunRecord record)
    {
        Assert.True(record.FirstHitTick > 0, "No hit landed.");
        Assert.True(record.EnemyDeathTick > 0, "The scripted enemy never died.");
        Assert.True(record.FractureTick > 0, "The cube never fractured.");
        Assert.True(record.RubblePieces > 0, "No rubble pieces.");
    }

    internal static ScriptStep Script(int tick, PersistentScriptedServer reference)
    {
        Dictionary<string, PlayerCommandFrame> commands = new(StringComparer.Ordinal);
        List<(string Session, IPlayerActionPayload Payload)> actions = [];
        bool walking = tick <= WalkUntil;
        bool fighting = tick >= FightFrom;
        Vector3? enemyAnchor = reference.TryGetScriptedEnemyAnchor();

        foreach (string session in Sessions)
        {
            bool isPrimary = session == PrimarySession;
            Vector3 facing = Vector3.UnitZ;
            bool attack = false;
            if (isPrimary && tick >= CubeAttackFrom)
            {
                facing = Vector3.UnitX;
                attack = true;
            }
            else if (!isPrimary && fighting)
            {
                if (enemyAnchor.HasValue)
                {
                    facing = DirectionFrom(reference.EyeOf(session), enemyAnchor.Value);
                    attack = true;
                }
                else if (reference.TryGetNearestEnemyAnchor(session, out Vector3 wolf))
                {
                    facing = DirectionFrom(reference.EyeOf(session), wolf);
                    attack = true;
                }
            }

            commands[session] = new PlayerCommandFrame(
                new PlayerSessionId(session),
                (ulong)tick,
                new PlayerModeSwitchIntent(fighting || (isPrimary && tick >= CubeAttackFrom)
                    ? PlayerInteractionMode.Combat
                    : PlayerInteractionMode.Building),
                // The primary stays at its spawn, so its +X attack ray hits the cube it built.
                new PlayerMoveIntent(walking && !isPrimary ? 1f : 0f, 0f),
                new PlayerLookIntent(0f, 0f, facing),
                PrimaryUsePressed: attack);
        }

        if (tick == BuildAt)
        {
            actions.Add((PrimarySession, new BuildUseIntent(reference.BuildCell)));
        }

        return new ScriptStep(commands, actions);
    }

    // Only the primary acts: it builds, then attacks its cube along +X every tick from tick 10.
    internal static ScriptStep CubeOnlyScript(int tick, PersistentScriptedServer reference)
    {
        Dictionary<string, PlayerCommandFrame> commands = new(StringComparer.Ordinal);
        foreach (string session in Sessions)
        {
            bool attacking = session == PrimarySession && tick >= 10;
            commands[session] = new PlayerCommandFrame(
                new PlayerSessionId(session),
                (ulong)tick,
                new PlayerModeSwitchIntent(attacking ? PlayerInteractionMode.Combat : PlayerInteractionMode.Building),
                PlayerMoveIntent.None,
                new PlayerLookIntent(0f, 0f, attacking ? Vector3.UnitX : Vector3.UnitZ),
                PrimaryUsePressed: attacking);
        }

        List<(string Session, IPlayerActionPayload Payload)> actions = tick == BuildAt
            ? [(PrimarySession, new BuildUseIntent(reference.BuildCell))]
            : [];
        return new ScriptStep(commands, actions);
    }

    internal static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? Vector3.UnitZ : direction.Normalized();
    }

    internal sealed record ScriptStep(
        IReadOnlyDictionary<string, PlayerCommandFrame> Commands,
        IReadOnlyList<(string Session, IPlayerActionPayload Payload)> Actions);

    internal sealed record RunRecord(
        IReadOnlyList<ulong> Checksums,
        IReadOnlyList<SimulationStateChecksum.Parts> Parts,
        IReadOnlyList<int> HitTicks,
        int FirstHitTick,
        int EnemyDeathTick,
        int FractureTick,
        int RubblePieces);

    // The production persistent host (deferred joins, persisted profiles, world and seed), driven
    // in-process one tick at a time so every command lands in the same tick on every run.
    internal sealed class PersistentScriptedServer : IDisposable
    {
        private readonly DedicatedServerHost _host;
        private readonly Dictionary<string, ulong> _actionSequences = new(StringComparer.Ordinal);
        private readonly EnemyActor _scriptedEnemy;
        private readonly List<ulong> _checksums = [];
        private readonly List<SimulationStateChecksum.Parts> _parts = [];
        private readonly List<int> _hitTicks = [];
        private int _fractureTick;
        private int _enemyDeathTick;
        private int _firstHitTick;

        public PersistentScriptedServer(ServerPersistenceBundle bundle)
            : this(ServerBootstrap.CreateAuthoritativeHost(bundle))
        {
            Assert.Equal(bundle.SimulationSeed, Seed);
        }

        public PersistentScriptedServer(DedicatedServerHost host)
        {
            _host = host;
            Runtime = _host.Runtime!;
            Seed = Runtime.SimulationSeed;

            foreach (string session in Sessions)
            {
                Assert.Empty(_host.HandleClientMessage($"connection-{session}", new ClientJoinRequestMessage(session, $"Name {session}", Dream.Tests.Support.TestAccountTokens.For($"Name {session}"))));
            }

            // The host defers a join until its player's saved state is loaded on the persistence
            // worker; let the loads finish first so every join applies on the first tick.
            Assert.True(Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);

            // Joins are applied by a tick of their own; script tick t runs as server tick t + 1.
            Assert.Equal(1, AdvanceOneTick());
            List<PendingConnectionReply> joinReplies = [];
            _host.DrainPendingJoinRepliesInto(joinReplies);
            Assert.Equal(Sessions.Length, joinReplies.Count);
            Assert.All(joinReplies, reply => Assert.IsType<JoinAcceptedMessage>(reply.Message));
            _host.DrainPendingActionResults();
            _ = _host.CreateSnapshotMessage();
            Assert.Equal(PrimarySession, Runtime.PrimaryRemoteSessionId?.Value);

            // On the startup floor (56 cells) generated terrain starts at x = 7 beside the spawn;
            // the primary builds in the free cell between itself and that terrain, on its +X ray.
            Vector3 primary = EyeOf(PrimarySession);
            BuildCell = new Vector3(MathF.Floor(primary.X) + 1f, 1f, MathF.Floor(primary.Z));
            Assert.Null(Runtime.Map.TryGetCubeAtCell((int)BuildCell.X, 1, (int)BuildCell.Z));

            Runtime.Enemies.Clear();
            EnemyCombatant combatant = new(
                () => EnemyPosition,
                eventHub: null,
                actorId: EnemyActorId,
                maxHealth: EnemyMaxHealth,
                attackDamage: 2,
                attackIntervalSeconds: 0.5f);
            _scriptedEnemy = new EnemyActor(combatant, EnemyPosition, aggroRange: 8f, attackRange: 3f, moveSpeed: 0f, displayName: "Scripted wolf");
            Runtime.Enemies.Add(_scriptedEnemy);
        }

        public ulong Seed { get; }

        public Vector3 BuildCell { get; }

        public int? BuiltCubeId { get; private set; }

        public int BuiltCubeFractureTick { get; private set; }

        // The built cube's health after each hit that damaged it.
        public List<(int Tick, int Health)> BuiltCubeHits { get; } = [];

        public WorldSessionRuntime Runtime { get; }

        public SimulationStateChecksum.Parts CurrentParts => SimulationStateChecksum.ComputeParts(Runtime);

        public RunRecord Record => new(
            _checksums,
            _parts,
            _hitTicks,
            _firstHitTick,
            _enemyDeathTick,
            _fractureTick,
            Runtime.Map.CaptureFracturePieces().Count());

        public ulong Hash() => SimulationStateChecksum.Compute(Runtime);

        public int FirstHitTick => _firstHitTick;

        public RunRecord RunScript(Func<int, PersistentScriptedServer, ScriptStep> script, int ticks = Ticks)
        {
            for (int tick = 1; tick <= ticks; tick++)
            {
                Advance(tick, script(tick, this));
            }

            return Record;
        }

        public Vector3? TryGetScriptedEnemyAnchor()
        {
            return _scriptedEnemy.Combatant.Health.IsAlive ? _scriptedEnemy.Combatant.GetCombatAnchorWorldPosition() : null;
        }

        public bool TryGetNearestEnemyAnchor(string session, out Vector3 anchor)
        {
            Vector3 eye = EyeOf(session);
            EnemyActor? nearest = Runtime.Enemies.ActiveActors
                .Where(enemy => enemy.Combatant.Health.IsAlive)
                .OrderBy(enemy => (enemy.Position - eye).LengthSquared)
                .FirstOrDefault();
            anchor = nearest?.Combatant.GetCombatAnchorWorldPosition() ?? default;
            return nearest != null;
        }

        public Vector3 EyeOf(string session)
        {
            ActorSnapshot actor = Runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == session);
            return actor.Position + new Vector3(0f, 0.35f, 0f);
        }

        public void Advance(int tick, ScriptStep step)
        {
            foreach (string session in step.Commands.Keys.Order(StringComparer.Ordinal))
            {
                _host.HandleClientMessage($"connection-{session}", new PlayerCommandMessage(step.Commands[session]));
            }

            foreach ((string session, IPlayerActionPayload payload) in step.Actions)
            {
                ulong sequence = _actionSequences.TryGetValue(session, out ulong last) ? last + 1 : 1;
                _actionSequences[session] = sequence;
                // Build use is server-originated only (NETR-6 rejects it over player.action), so the
                // script queues it on the host's tick-drained action queue directly.
                Assert.True(_host.TryEnqueueServerAction(new PlayerActionMessage(new PlayerSessionId(session), sequence, payload)));
            }

            Assert.Equal(1, AdvanceOneTick());
            Assert.Equal((ulong)tick + 1, Runtime.CurrentServerTick);

            // A seeded damage roll happened this tick when any attack (on an enemy or a cube) did
            // damage; the enemies' own hits on players are not rolled.
            foreach (AuthoritativeActionResult result in _host.DrainPendingActionResults())
            {
                if (result.Accepted && result.Payload is CombatResolutionResult { DamageApplied: > 0 } combat)
                {
                    if (_hitTicks.Count == 0 || _hitTicks[^1] != tick)
                    {
                        _hitTicks.Add(tick);
                    }

                    if (BuiltCubeId.HasValue && combat.TargetActorId == BuiltCubeId)
                    {
                        BuiltCubeHits.Add((tick, combat.CurrentHealth));
                    }
                }
            }

            // Deltas are exported every tick, like the service does for joined connections.
            ServerSnapshotMessage snapshot = _host.CreateSnapshotMessage();
            foreach (WorldMutationDelta mutation in snapshot.Snapshot.WorldMutations)
            {
                if (mutation.Kind == WorldMutationKind.CubePlaced && mutation.CellPosition == BuildCell)
                {
                    BuiltCubeId = mutation.ActorId;
                }
                else if (mutation.Kind == WorldMutationKind.CubeFractured && mutation.CellPosition == BuildCell)
                {
                    BuiltCubeFractureTick = tick;
                }
            }

            if (_fractureTick == 0 && Runtime.Map.CaptureFracturePieces().Any())
            {
                _fractureTick = tick;
            }

            if (_firstHitTick == 0 && _scriptedEnemy.Combatant.Health.CurrentHealth < EnemyMaxHealth)
            {
                _firstHitTick = tick;
            }

            if (_enemyDeathTick == 0 && !_scriptedEnemy.Combatant.Health.IsAlive)
            {
                _enemyDeathTick = tick;
            }

            SimulationStateChecksum.Parts parts = SimulationStateChecksum.ComputeParts(Runtime);
            _parts.Add(parts);
            _checksums.Add(parts.Combined);
        }

        public void Dispose()
        {
            _host.Dispose();
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

    // A temp SQLite world opened through the production bootstrap with the shipped init script.
    internal sealed class TempDatabase : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly string _schemaPath;

        public TempDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-persistent-seed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            _schemaPath = Path.Combine(RepoRoot.Find(), "dream", "config", "player-profiles.init.sql");
            DatabasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
        }

        public string DatabasePath { get; }

        public ServerPersistenceBundle OpenBundle()
        {
            return ServerBootstrap.CreatePersistenceBundle(DatabasePath, _schemaPath);
        }

        public long Count(string table)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        // Empties every table except worlds (the seed and the clock), world_metadata and combat_formulas
        // (rules data the init script seeds), so the next load starts a fresh world that keeps its
        // stored seed.
        public void WipeWorldStateKeepingMetadata()
        {
            using SqliteConnection connection = Open();
            List<string> tables = [];
            using (SqliteCommand list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
                using SqliteDataReader reader = list.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            Assert.Contains("worlds", tables);
            using SqliteCommand wipe = connection.CreateCommand();
            wipe.CommandText = "PRAGMA foreign_keys = OFF; " + string.Concat(
                tables.Where(table => table is not "worlds" and not "world_metadata" and not "combat_formulas" and not "schema_version")
                    .Select(table => $"DELETE FROM \"{table}\"; "));
            wipe.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
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

internal static class RepoRoot
{
    public static string Find()
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
}
