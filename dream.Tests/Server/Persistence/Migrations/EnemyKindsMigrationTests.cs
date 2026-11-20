using System.Globalization;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Migration 0017: the global enemy_kinds table (per-kind chase speed, patrol speed, attack interval,
// attack range, aggro range) the server applies to new spawns and restored enemies, and the rewrite of
// the speed columns of stored enemy rows, which is gated on the kind and never on a value.
public sealed class EnemyKindsMigrationTests
{
    private const string InsertEnemy =
        "INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, " +
        "facing_x, facing_y, facing_z, current_hp, max_hp, strength, agility, vitality, energy, movement_speed, attack_speed, level) VALUES ";

    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    private static List<EnemyKindTuning> ReadRows(MigrationTestDatabase database)
    {
        SqliteEnemyKindRepository repository = new(database.Schema);
        return [.. repository.LoadAll()];
    }

    [Fact]
    public void AFreshDatabase_HasOneRowPerShippedKind_EqualToEnemiesJson()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();

        List<EnemyKindTuning> rows = ReadRows(database);

        Assert.Equal(ContentCatalog.Shipped.Enemies.Count, rows.Count);
        foreach (EnemyTemplate template in ContentCatalog.Shipped.Enemies)
        {
            EnemyKindTuning row = Assert.Single(rows, candidate => candidate.EnemyKind == template.Key);
            Assert.NotNull(template.AttackIntervalSeconds);
            Assert.Equal(template.MoveSpeed, row.ChaseSpeed);
            Assert.Equal(template.PatrolSpeed, row.PatrolSpeed);
            Assert.Equal(template.AttackIntervalSeconds!.Value, row.AttackIntervalSeconds);
            Assert.Equal(template.AttackRange, row.AttackRange);
            Assert.Equal(template.AggroRange, row.AggroRange);
            // enemies.json's attackSpeed is the interval's reciprocal (6 decimals), which is what a spawn writes.
            Assert.Equal(1f / template.AttackIntervalSeconds.Value, template.AttackSpeed, 5);
            Assert.True(row.IsValid(out string reason), reason);
        }
    }

    [Fact]
    public void AV16Database_IsRewrittenByKind_AndAnUnknownKindIsLeftAlone()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(16));
        database.ExecuteScript(
            InsertEnemy +
            "(1, 21, 'Wolf', 'Wolf', 5.5, 1, 2.5, 0, 0, 1, 100, 100, 10, 10, 10, 0, 5.0, 0.95238096, 1), " +
            // A value nobody authored: the rewrite is gated on the kind, so this is rewritten too.
            "(1, 22, 'Wolf', 'Wolf', 1, 1, 1, 0, 0, 1, 100, 100, 10, 10, 10, 0, 123.0, 4.5, 1), " +
            "(1, 23, 'DireWolf', 'Dire Wolf', 1, 1, 1, 0, 0, -1, 10, 200, 16, 10, 30, 0, 5.6, 1, 3), " +
            "(1, 24, 'Mystery', 'Mystery', 1, 1, 1, 0, 0, -1, 10, 200, 16, 10, 30, 0, 7.7, 0.3, 3);");

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(16, result.FromVersion);
        Assert.Equal(MigrationTestDatabase.ShippedLatestVersion, result.ToVersion);
        using SqliteConnection c = database.Open();
        (double Speed, double Attack) Row(long id)
        {
            using SqliteCommand command = c.CreateCommand();
            command.CommandText = $"SELECT movement_speed, attack_speed FROM enemy_instances WHERE object_id = {id};";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetDouble(0), reader.GetDouble(1));
        }

        ContentCatalog.Shipped.TryGetEnemy("Wolf", out EnemyTemplate wolf);
        ContentCatalog.Shipped.TryGetEnemy("DireWolf", out EnemyTemplate dire);
        foreach (long wolfId in new long[] { 21, 22 })
        {
            (double speed, double attack) = Row(wolfId);
            Assert.Equal(wolf.MoveSpeed, speed, 5);
            Assert.Equal(1.0 / wolf.AttackIntervalSeconds!.Value, attack, 5);
        }

        (double direSpeed, double direAttack) = Row(23);
        Assert.Equal(dire.MoveSpeed, direSpeed, 5);
        Assert.Equal(1.0 / dire.AttackIntervalSeconds!.Value, direAttack, 5);

        // An unknown kind keeps exactly what it was stored with.
        (double mysterySpeed, double mysteryAttack) = Row(24);
        Assert.Equal(7.7, mysterySpeed, 9);
        Assert.Equal(0.3, mysteryAttack, 9);
    }

    [Fact]
    public void RunningTheScriptAgain_KeepsAHandEditedRow()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        database.ExecuteScript("UPDATE enemy_kinds SET chase_speed = 2.0 WHERE enemy_kind = 'Wolf';");

        database.ExecuteScript(File.ReadAllText(Path.Combine(MigrationTestDatabase.MigrationsDirectory, "0017_enemy_kinds.sql")));

        Assert.Equal(2.0, Convert.ToDouble(database.Scalar("SELECT chase_speed FROM enemy_kinds WHERE enemy_kind = 'Wolf';"), CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("UPDATE enemy_kinds SET patrol_speed = 9 WHERE enemy_kind = 'Wolf';")]
    [InlineData("UPDATE enemy_kinds SET chase_speed = 0 WHERE enemy_kind = 'Wolf';")]
    [InlineData("UPDATE enemy_kinds SET attack_interval_seconds = 0.01 WHERE enemy_kind = 'Wolf';")]
    [InlineData("UPDATE enemy_kinds SET attack_range = 0 WHERE enemy_kind = 'Wolf';")]
    [InlineData("UPDATE enemy_kinds SET aggro_range = -1 WHERE enemy_kind = 'Wolf';")]
    public void TheTableRejectsARowThatBreaksItsChecks(string sql)
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();

        Assert.Throws<SqliteException>(() => database.ExecuteScript(sql));
    }

    // The tuning a designer edits in SQL reaches the game: the edited row changes a NEW wolf and a
    // RESTORED wolf alike, through the same bootstrap path the server uses.
    [Fact]
    public void ADatabaseEdit_ChangesBothASpawnedAndARestoredWolf()
    {
        using MigrationTestDatabase spawnDatabase = new();
        using MigrationTestDatabase restoreDatabase = new();
        spawnDatabase.Schema.EnsureSchema();
        restoreDatabase.Schema.EnsureSchema();
        const string edit =
            "UPDATE enemy_kinds SET chase_speed = 2.0, patrol_speed = 0.5, attack_interval_seconds = 2.5 WHERE enemy_kind = 'Wolf';";
        spawnDatabase.ExecuteScript(edit);
        restoreDatabase.ExecuteScript(edit);
        List<string> log = [];

        // A new spawn: an empty world's runtime spawns its first wolf from the tuned content.
        ContentCatalog tunedForSpawn = ServerBootstrap.ApplyEnemyKindTuning(ReadRows(spawnDatabase), log.Add);
        using (WorldSessionRuntime runtime = CreateRuntime(spawnDatabase, tunedForSpawn))
        {
            EnemyActor spawned = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.Equal(2.0f, spawned.MoveSpeed);
            Assert.Equal(0.5f, spawned.PatrolSpeed);
            Assert.Equal(2.5f, spawned.Combatant.AttackIntervalSeconds);
        }

        // A restored wolf: a stored row (chase speed and attack speed of the old balance) is rebuilt at the kind's tuning.
        SqliteWorldPersistenceRepository world = new(restoreDatabase.Schema);
        world.SaveEnemyInstance(WolfRow(chaseSpeedOnRow: 5.0f));
        ContentCatalog tunedForRestore = ServerBootstrap.ApplyEnemyKindTuning(ReadRows(restoreDatabase), log.Add);
        using (WorldSessionRuntime runtime = CreateRuntime(restoreDatabase, tunedForRestore))
        {
            EnemyActor restored = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.Equal(2.0f, restored.MoveSpeed);
            Assert.Equal(0.5f, restored.PatrolSpeed);
            Assert.Equal(2.5f, restored.Combatant.AttackIntervalSeconds);
        }

        Assert.Contains($"server.content.enemy-tuning rows={WorldSessionRuntime.DefaultEnemyContent.Enemies.Count} skipped=0", log);
    }

    // The negative of the test above: with no edit the same path gives the shipped numbers, so the
    // assertions there can fail.
    [Fact]
    public void WithoutAnEdit_TheTunedCatalog_HasTheShippedNumbers()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();

        ContentCatalog tuned = ServerBootstrap.ApplyEnemyKindTuning(ReadRows(database), null);

        foreach (EnemyTemplate shipped in WorldSessionRuntime.DefaultEnemyContent.Enemies)
        {
            Assert.True(tuned.TryGetEnemy(shipped.Key, out EnemyTemplate? template));
            Assert.Equal(shipped.MoveSpeed, template.MoveSpeed);
            Assert.Equal(shipped.PatrolSpeed, template.PatrolSpeed);
            Assert.Equal(shipped.AttackIntervalSeconds, template.AttackIntervalSeconds);
        }

        Assert.Equal(WorldSessionRuntime.DefaultEnemyContent.ContentDigest, tuned.ContentDigest);
    }

    [Fact]
    public void AnInvalidOrUnknownRow_IsSkippedWithALogLine_AndTheKindKeepsItsAuthoredNumbers()
    {
        List<string> log = [];
        List<EnemyKindTuning> rows =
        [
            new("Wolf", 2.0f, 0.5f, 2.5f, 1.35f, 10f),
            // Patrol above chase.
            new("DireWolf", 3.3f, 9f, 1.6f, 1.35f, 12f),
            // Not finite.
            new("AshenProwler", float.NaN, 1.2f, 1.5f, 1.35f, 12f),
            // Inside the player separation, where the wolf could never bite.
            new("GraveHowler", 3.8f, 1.3f, 1.4f, 0.5f, 14f),
            // The risen skeleton's reach is zeroed: invalid, so the authored 1.5 stays.
            new("RisenSkeleton", 3.4f, 1.15f, 1.55f, 0f, 12f),
            // A kind the content does not have.
            new("Dragon", 3f, 1f, 1f, 2f, 10f),
        ];

        ContentCatalog tuned = ServerBootstrap.ApplyEnemyKindTuning(rows, log.Add);

        Assert.Equal(2.0f, tuned.Wolf.MoveSpeed);
        foreach (string kind in new[] { "DireWolf", "AshenProwler", "GraveHowler", "RisenSkeleton" })
        {
            WorldSessionRuntime.DefaultEnemyContent.TryGetEnemy(kind, out EnemyTemplate shipped);
            Assert.True(tuned.TryGetEnemy(kind, out EnemyTemplate? kept));
            Assert.Equal(shipped, kept);
        }

        Assert.Contains("server.content.enemy-tuning rows=1 skipped=5", log);
        Assert.Equal(5, log.Count(line => line.StartsWith("server.content.enemy-tuning-skipped", StringComparison.Ordinal)));
    }

    // Every chase is faster than the player's walk and slower than the player's sprint, so a wolf catches a
    // walking player and Shift always escapes.
    [Fact]
    public void EveryShippedChase_IsFasterThanThePlayersWalk_AndSlowerThanThePlayersSprint()
    {
        const float playerCruise = PlayerSimulationTuning.DefaultRunSpeed;
        const float playerSprint = PlayerSimulationTuning.DefaultSprintSpeed;
        static bool CatchesAWalkerAndLosesToASprinter(float chase) => chase > playerCruise && chase < playerSprint;
        Assert.All(ContentCatalog.Shipped.Enemies, template =>
            Assert.True(CatchesAWalkerAndLosesToASprinter(template.MoveSpeed), $"{template.Key} chases at {template.MoveSpeed}"));

        // The synthetic negatives, through the same predicate: the old chase speed outruns the sprint,
        // and a chase at the walk cannot catch a walker.
        Assert.False(CatchesAWalkerAndLosesToASprinter(5.0f));
        Assert.False(CatchesAWalkerAndLosesToASprinter(playerCruise));
    }

    private static PersistedEnemyInstance WolfRow(float chaseSpeedOnRow) =>
        new(
            new ObjectId(2000),
            "Wolf",
            "Wolf",
            new Vector3(9f, 1f, 9f),
            Vector3.UnitZ,
            new PersistedCreatureStats(100, 100, 10, 10, 10, 0, chaseSpeedOnRow, 0.95238096f, 1),
            DateTime.UtcNow,
            DateTime.UtcNow);

    private static WorldSessionRuntime CreateRuntime(MigrationTestDatabase database, ContentCatalog enemyContent)
    {
        WorldSessionRuntime runtime = new(
            WorldDescriptor.ForFloorSize(20),
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Background(),
            enemyContent: enemyContent);
        runtime.WriteCheckpointOnDispose = false;
        return runtime;
    }
}
