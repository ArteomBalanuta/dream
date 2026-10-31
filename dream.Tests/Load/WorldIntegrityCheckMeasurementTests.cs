using System.Diagnostics;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// Wave F measurement (DREAM_LOAD_TEST=1, Release): what the load-time integrity check costs on the
// seeded World10k database, with and without PRAGMA quick_check, and what a whole persistence
// bundle open (content read, schema, world resolve, lease, check) costs. The database is migrated
// by the first open, so every measured open is a warm restart of an existing file.
[Collection(TimingSensitiveCollection.Name)]
public sealed class WorldIntegrityCheckMeasurementTests
{
    private const int Samples = 10;

    private readonly ITestOutputHelper _output;

    public WorldIntegrityCheckMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LoadFact]
    public void World10k_IntegrityCheckAndBundleOpenCost()
    {
        LoadScenario scenario = LoadScenario.World10k;
        string root = LoadPaths.FindRepoRoot();
        string work = Path.Combine(Path.GetTempPath(), $"dream-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        string databasePath = Path.Combine(work, "world.sqlite");
        string schemaPath = Path.Combine(root, "dream", "config", "player-profiles.init.sql");
        try
        {
            WorldSeeder.Seed(databasePath, schemaPath, scenario);
            WorldDescriptor descriptor;
            using (ServerPersistenceBundle first = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath))
            {
                descriptor = first.Descriptor!;
            }

            SqliteSchemaBootstrapper schema = new(databasePath, schemaPath);
            WorldIntegrityOptions quick = ServerBootstrap.IntegrityOptionsFor(Dream.Simulation.Content.ContentCatalog.Shipped);
            WorldIntegrityOptions noQuick = quick with { QuickCheck = false };
            double withQuick = MeasureCheck(schema, descriptor, quick);
            double withoutQuick = MeasureCheck(schema, descriptor, noQuick);

            List<double> opens = [];
            for (int i = 0; i < Samples + 2; i++)
            {
                long started = Stopwatch.GetTimestamp();
                using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath);
                if (i >= 2)
                {
                    opens.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }

            long length = new FileInfo(databasePath).Length;
            _output.WriteLine(
                $"World10k db {length} B: integrity check with quick_check {withQuick:0.00} ms mean, " +
                $"without {withoutQuick:0.00} ms mean, bundle open (whole, includes the check) {opens.Average():0.00} ms mean / {opens.Min():0.00} min");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, recursive: true);
        }
    }

    private static double MeasureCheck(SqliteSchemaBootstrapper schema, WorldDescriptor descriptor, WorldIntegrityOptions options)
    {
        List<double> samples = [];
        for (int i = 0; i < Samples + 2; i++)
        {
            using SqliteConnection connection = schema.CreateOpenConnection();
            WorldIntegrityReport report = WorldIntegrityCheck.Run(connection, descriptor, options);
            Assert.False(report.IsFatal);
            if (i >= 2)
            {
                samples.Add(report.Elapsed.TotalMilliseconds);
            }
        }

        return samples.Average();
    }
}
