using System.Globalization;
using System.Security.Cryptography;
using Dream.Simulation.Runtime;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The seed a persistent world rolls with, and whether this load created it.
public readonly record struct WorldSimulationSeed(ulong Seed, bool Created);

// Key/value metadata about the persisted world, stored in the world_metadata table that
// SqliteSchemaBootstrapper.EnsureSchema creates: the simulation seed and the world clock.
public sealed class SqliteWorldMetadataRepository
{
    public const string SimulationSeedKey = "simulation_seed";

    // WorldClock.WorldTicks as a decimal integer; written by SqlitePersistenceCheckpointWriter.
    public const string WorldTimeTicksKey = "world_time_ticks";

    private readonly SqliteSchemaBootstrapper _schema;

    public SqliteWorldMetadataRepository(SqliteSchemaBootstrapper schema)
    {
        _schema = schema;
    }

    // A cryptographically random 64-bit seed. Only called when a world is created (or when an
    // older database without a stored seed loads for the first time), never per roll. It never
    // returns the public DefaultSimulationSeed, so a persisted world never rolls like an
    // ephemeral host.
    public static ulong GenerateSimulationSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong seed;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            seed = BitConverter.ToUInt64(bytes);
        }
        while (seed == WorldSessionRuntime.DefaultSimulationSeed);

        return seed;
    }

    public bool TryGetSimulationSeed(out ulong seed)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        return TryReadSimulationSeed(connection, out seed);
    }

    // Returns the stored seed, or generates, stores and returns a new one when the world has none.
    // INSERT OR IGNORE keeps the first stored seed if two loads race on the same database.
    public WorldSimulationSeed GetOrCreateSimulationSeed(Func<ulong>? generator = null)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        if (TryReadSimulationSeed(connection, out ulong existing))
        {
            return new WorldSimulationSeed(existing, Created: false);
        }

        ulong generated = (generator ?? GenerateSimulationSeed)();
        int inserted;
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT OR IGNORE INTO world_metadata (key, value) VALUES ($key, $value);";
            insert.Parameters.AddWithValue("$key", SimulationSeedKey);
            insert.Parameters.AddWithValue("$value", FormatSeed(generated));
            inserted = insert.ExecuteNonQuery();
        }

        if (inserted == 1)
        {
            return new WorldSimulationSeed(generated, Created: true);
        }

        if (!TryReadSimulationSeed(connection, out ulong raced))
        {
            throw new InvalidOperationException("World simulation seed could not be stored or read.");
        }

        return new WorldSimulationSeed(raced, Created: false);
    }

    public static string FormatSeed(ulong seed)
    {
        return seed.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatWorldTimeTicks(long ticks)
    {
        return ticks.ToString(CultureInfo.InvariantCulture);
    }

    // False for a world that has never written a checkpoint (it starts at WorldTicks 0).
    public bool TryGetWorldTimeTicks(out long ticks)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM world_metadata WHERE key = $key;";
        select.Parameters.AddWithValue("$key", WorldTimeTicksKey);
        object? value = select.ExecuteScalar();
        if (value is null or DBNull)
        {
            ticks = 0;
            return false;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
        {
            // Unlike the seed, a lost time of day only moves the sun; fail loudly anyway so a
            // corrupted row is noticed rather than silently reset on the next checkpoint.
            throw new InvalidDataException(
                $"world_metadata.{WorldTimeTicksKey} holds '{text}', which is not a non-negative 64-bit integer.");
        }

        return true;
    }

    private static bool TryReadSimulationSeed(SqliteConnection connection, out ulong seed)
    {
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM world_metadata WHERE key = $key;";
        select.Parameters.AddWithValue("$key", SimulationSeedKey);
        object? value = select.ExecuteScalar();
        if (value is null or DBNull)
        {
            seed = 0;
            return false;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seed))
        {
            // Regenerating would silently change every roll in the world; fail loudly instead.
            throw new InvalidDataException(
                $"world_metadata.{SimulationSeedKey} holds '{text}', which is not an unsigned 64-bit integer.");
        }

        return true;
    }
}
