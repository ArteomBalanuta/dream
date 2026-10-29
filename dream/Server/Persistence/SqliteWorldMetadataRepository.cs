using System.Globalization;
using System.Security.Cryptography;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The seed a persistent world rolls with, and whether this load created it.
public readonly record struct WorldSimulationSeed(ulong Seed, bool Created);

// The per-world metadata held on the worlds row (migration 0003): the simulation seed and the world
// clock. The repository is bound to one world; world_metadata stays a generic key/value table for
// global facts.
public sealed partial class SqliteWorldMetadataRepository : SqliteRepositoryBase
{
    private const string SelectSeedSql = "SELECT seed FROM worlds WHERE world_id = $worldId;";

    private const string SelectWorldTimeSql = "SELECT world_time_ticks FROM worlds WHERE world_id = $worldId;";

    // Only stores a seed when the world has none, so the first stored seed wins if two loads race.
    private const string StoreSeedSql = "UPDATE worlds SET seed = $seed WHERE world_id = $worldId AND seed IS NULL;";

    // World 1: the default world.
    public SqliteWorldMetadataRepository(SqliteSchemaBootstrapper schema)
        : this(schema, WorldId.Default)
    {
    }

    // Bound to one world.
    public SqliteWorldMetadataRepository(SqliteSchemaBootstrapper schema, WorldId worldId)
        : base(new SqliteWorldStore(schema), worldId)
    {
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
        using SqliteSession session = Store.OpenSession();
        return TryReadSimulationSeed(session, WorldIdValue, out seed);
    }

    // Returns the stored seed, or generates, stores and returns a new one when the world has none.
    // The UPDATE only fills a NULL seed, so the first stored seed wins if two loads race on the same
    // database.
    public WorldSimulationSeed GetOrCreateSimulationSeed(Func<ulong>? generator = null)
    {
        // One session for the read, the update and the re-read, as one connection lease today.
        using SqliteSession session = Store.OpenSession();
        long worldId = WorldIdValue;
        if (TryReadSimulationSeed(session, worldId, out ulong existing))
        {
            return new WorldSimulationSeed(existing, Created: false);
        }

        ulong generated = (generator ?? GenerateSimulationSeed)();
        int updated;
        using (SqliteCommand store = session.CreateCommand(StoreSeedSql))
        {
            store.BindText("$seed", FormatSeed(generated));
            store.BindInt64("$worldId", worldId);
            updated = store.ExecuteNonQuery();
        }

        if (updated == 1)
        {
            return new WorldSimulationSeed(generated, Created: true);
        }

        if (!TryReadSimulationSeed(session, worldId, out ulong raced))
        {
            throw new InvalidOperationException($"World {worldId} has no simulation seed and none could be stored.");
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

    // False for a world that has never written a checkpoint (it starts at WorldTicks 0), or that does
    // not exist.
    public bool TryGetWorldTimeTicks(out long ticks)
    {
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand select = session.CreateCommand(SelectWorldTimeSql);
        select.BindInt64("$worldId", WorldIdValue);
        object? value = select.ExecuteScalar();
        if (value is null or DBNull)
        {
            ticks = 0;
            return false;
        }

        // Unlike the seed, a lost time of day only moves the sun; fail loudly anyway so a corrupted
        // row is noticed rather than silently reset on the next checkpoint. The column has INTEGER
        // affinity, so text that is not a number is kept as text and rejected here.
        if (value is not long stored || stored < 0)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            throw new InvalidDataException(
                $"worlds.world_time_ticks of world {WorldIdValue} holds '{text}', which is not a non-negative 64-bit integer.");
        }

        // The column defaults to 0, which is also the start of a world that never wrote a checkpoint.
        ticks = stored;
        return stored != 0;
    }

    private static bool TryReadSimulationSeed(SqliteSession session, long worldId, out ulong seed)
    {
        using SqliteCommand select = session.CreateCommand(SelectSeedSql);
        select.BindInt64("$worldId", worldId);
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
                $"worlds.seed of world {worldId} holds '{text}', which is not an unsigned 64-bit integer.");
        }

        return true;
    }
}
