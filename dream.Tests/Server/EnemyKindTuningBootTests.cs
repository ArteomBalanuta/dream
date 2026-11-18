using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server;

// The enemy_kinds tuning on the real start path: CreatePersistenceBundle -> CreateRuntimeHost
// (CreatePorts -> RuntimeAuthoritativeSessionHost.CreateDefault) on a temp database, with nothing
// overwriting Runtime.EnemyContent in between, so a broken wiring line in any of the three hand-offs
// leaves the wolf at its authored numbers and fails this test.
public sealed class EnemyKindTuningBootTests
{
    [Fact]
    public void ADatabaseEdit_ReachesTheLiveWolf_ThroughTheRealStartPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dream-enemy-tuning-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "world.sqlite");
        string schemaPath = Path.Combine(Dream.Tests.Load.LoadPaths.FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
        try
        {
            // The shipped numbers first, so the edit below is a real change.
            (float untouchedSpeed, float untouchedInterval) = BootAndReadTheWolf(databasePath, schemaPath);
            Assert.NotEqual(2.0f, untouchedSpeed);
            Assert.NotEqual(2.5f, untouchedInterval);

            SqliteSchemaBootstrapper schema = new(databasePath, schemaPath);
            using (SqliteConnection connection = schema.CreateOpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "UPDATE enemy_kinds SET chase_speed = 2.0, attack_interval_seconds = 2.5 WHERE enemy_kind = 'Wolf';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            (float speed, float interval) = BootAndReadTheWolf(databasePath, schemaPath);
            Assert.Equal(2.0f, speed);
            Assert.Equal(2.5f, interval);
        }
        finally
        {
            try
            {
                SqlitePools.Release(databasePath);
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // Reads the numbers off the live wolf and releases the world.
    private static (float MoveSpeed, float AttackInterval) BootAndReadTheWolf(string databasePath, string schemaPath)
    {
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(
            ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath),
            null);
        EnemyActor wolf = Assert.Single(host.Runtime.Enemies.ActiveActors, actor => actor.EnemyKind == "Wolf");
        return (wolf.MoveSpeed, wolf.Combatant.AttackIntervalSeconds);
    }

    [Fact]
    public void ASeedEqualRow_LeavesTheTemplateRecordEqual()
    {
        foreach (EnemyTemplate authored in ContentCatalog.Shipped.Enemies)
        {
            EnemyKindTuning row = new(
                authored.Key,
                authored.MoveSpeed,
                authored.PatrolSpeed,
                authored.AttackIntervalSeconds!.Value,
                authored.AttackRange,
                authored.AggroRange);

            ContentCatalog tuned = ContentCatalog.Shipped.WithEnemyTuning([row]);

            Assert.True(tuned.TryGetEnemy(authored.Key, out EnemyTemplate? template));
            Assert.Equal(authored, template);
        }

        // The negative: a row that changes the interval does change the template.
        EnemyTemplate wolf = ContentCatalog.Shipped.Wolf;
        EnemyKindTuning slower = new(wolf.Key, wolf.MoveSpeed, wolf.PatrolSpeed, wolf.AttackIntervalSeconds!.Value * 2f, wolf.AttackRange, wolf.AggroRange);
        Assert.NotEqual(wolf, ContentCatalog.Shipped.WithEnemyTuning([slower]).Wolf);
    }

    [Fact]
    public void TheTuningBuildsOnTheContentTheStartValidated()
    {
        EnemyTemplate wolf = ContentCatalog.Shipped.Wolf;
        ContentCatalog other = ContentCatalog.Shipped.WithEnemyTuning([]);
        List<EnemyKindTuning> rows = [new(wolf.Key, 2.0f, 0.5f, 2.5f, wolf.AttackRange, wolf.AggroRange)];

        ContentCatalog tuned = ServerBootstrap.ApplyEnemyKindTuning(rows, null, other);

        Assert.Equal(2.0f, tuned.Wolf.MoveSpeed);
        Assert.Equal(other.Camps.Count, tuned.Camps.Count);
    }

    [Fact]
    public void ARowInsideTheGaitBand_WarnsWithoutBeingRejected()
    {
        List<string> log = [];
        EnemyTemplate wolf = ContentCatalog.Shipped.Wolf;
        float deadband = EnemyVisualCatalog.GaitThresholdSpeed - (EnemyVisualCatalog.GaitHysteresisSpeed / 2f);
        EnemyKindTuning inBand = new(wolf.Key, deadband, 0.5f, wolf.AttackIntervalSeconds!.Value, wolf.AttackRange, wolf.AggroRange);

        ContentCatalog tuned = ServerBootstrap.ApplyEnemyKindTuning([inBand], log.Add);

        Assert.Equal(deadband, tuned.Wolf.MoveSpeed);
        Assert.Contains(log, line => line.StartsWith("server.content.enemy-tuning-warning", StringComparison.Ordinal) && line.Contains("chase_speed=1.8"));
        Assert.Contains(log, line => line.StartsWith("server.content.enemy-tuning rows=1 skipped=0", StringComparison.Ordinal));
    }

    [Fact]
    public void APatrolInsideTheGaitBand_AndAChaseBeyondTheTeleportDistance_Warn()
    {
        EnemyTemplate wolf = ContentCatalog.Shipped.Wolf;
        float band = EnemyVisualCatalog.GaitThresholdSpeed - (EnemyVisualCatalog.GaitHysteresisSpeed / 2f);

        List<string> patrol = [];
        ServerBootstrap.WarnWhenOutsideTheClientBand(
            new EnemyKindTuning(wolf.Key, band + 3f, band, 1f, wolf.AttackRange, wolf.AggroRange), patrol.Add);
        Assert.Contains(patrol, line => line.Contains("patrol_speed="));

        List<string> teleport = [];
        float fast = ClientScenePresenter.EnemyTeleportDistance + 1f;
        ServerBootstrap.WarnWhenOutsideTheClientBand(
            new EnemyKindTuning(wolf.Key, fast, 1f, 1f, wolf.AttackRange, wolf.AggroRange), teleport.Add);
        Assert.Contains(teleport, line => line.Contains("teleport distance"));
    }

    // ResolveSprinting starts the gallop at the threshold and drops it below threshold - hysteresis, so only
    // the speeds strictly between the two are ambiguous; at or above the threshold is a plain gallop.
    [Theory]
    [InlineData(2.2f, false)]
    [InlineData(2.4f, false)]
    [InlineData(2.0f, false)]
    [InlineData(1.6f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1.8f, true)]
    [InlineData(1.95f, true)]
    public void AChaseSpeed_WarnsOnlyInsideTheRealGaitDeadband(float chase, bool warns)
    {
        EnemyTemplate wolf = ContentCatalog.Shipped.Wolf;
        List<string> log = [];

        ServerBootstrap.WarnWhenOutsideTheClientBand(
            new EnemyKindTuning(wolf.Key, chase, 0.5f, 1f, wolf.AttackRange, wolf.AggroRange), log.Add);

        Assert.Equal(warns, log.Any(line => line.Contains("chase_speed=")));
        // The same speed against the real resolver: inside the deadband the clip depends on history.
        EnemyClipSet clips = EnemyVisualCatalog.WolfClips;
        Assert.Equal(warns, clips.ResolveSprinting(chase, false) != clips.ResolveSprinting(chase, true));
    }

    [Fact]
    public void TheShippedRows_DoNotWarn()
    {
        List<string> log = [];
        foreach (EnemyTemplate template in ContentCatalog.Shipped.Enemies)
        {
            ServerBootstrap.WarnWhenOutsideTheClientBand(
                new EnemyKindTuning(
                    template.Key,
                    template.MoveSpeed,
                    template.PatrolSpeed,
                    template.AttackIntervalSeconds!.Value,
                    template.AttackRange,
                    template.AggroRange),
                log.Add);
        }

        Assert.Empty(log);
    }
}
