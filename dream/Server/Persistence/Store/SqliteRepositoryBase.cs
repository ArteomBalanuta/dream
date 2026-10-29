using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Store;

// Base for the SQLite repositories. Each helper opens one session per call through
// IWorldStore.OpenSession (so it enlists in an ambient SqliteWriteBatch), runs one statement and
// disposes the session.
//
// A world-bound repository (the (store, worldId) constructor) owns the rows of one world: every helper
// binds $worldId before the caller's bind runs, and every statement of such a repository filters or
// keys on world_id = $worldId. A repository built with the (store) constructor binds nothing.
public abstract class SqliteRepositoryBase(IWorldStore store, WorldId? worldId)
{
    protected SqliteRepositoryBase(IWorldStore store)
        : this(store, null)
    {
    }

    protected IWorldStore Store { get; } = store;

    protected WorldId? BoundWorld { get; } = worldId;

    // The world id every statement is scoped to. Only for world-bound repositories.
    protected long WorldIdValue =>
        (BoundWorld ?? throw new InvalidOperationException("This repository is not bound to a world.")).Value;

    private void BindWorld(SqliteCommand command)
    {
        if (BoundWorld is { } world)
        {
            command.Parameters.AddWithValue("$worldId", world.Value);
        }
    }

    // State-passing overloads so callers can use static lambdas (no closure allocation on hot paths).
    protected int Execute<TState>(string sql, TState state, Action<SqliteCommand, TState> bind)
    {
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(sql);
        BindWorld(command);
        bind(command, state);
        return command.ExecuteNonQuery();
    }

    // The first row, read with read, or null when the query returns no row.
    protected T? QuerySingle<TState, T>(
        string sql,
        TState state,
        Action<SqliteCommand, TState> bind,
        Func<SqliteDataReader, T> read)
        where T : struct
    {
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(sql);
        BindWorld(command);
        bind(command, state);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? read(reader) : null;
    }

    protected List<T> QueryList<T>(string sql, Func<SqliteDataReader, T> read)
    {
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(sql);
        BindWorld(command);
        using SqliteDataReader reader = command.ExecuteReader();
        List<T> rows = [];
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    // The scalar result as a long (Convert.ToInt64, as the repositories do today).
    protected long QueryInt64(string sql)
    {
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(sql);
        BindWorld(command);
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

// SQL text helpers shared by the repositories.
internal static class SqliteRepositorySql
{
    // The checkpoint statement with its $worldId and $updatedUtc parameters replaced by literals.
    // The checkpoint statements run once per row and Microsoft.Data.Sqlite looks every parameter up
    // by name and binds it on every execution; the two values that never change within a statement
    // cost a lookup and a bind per row as parameters and nothing as literals. The world id is a
    // long formatted invariantly and the time is escaped as a SQL string, so neither can inject
    // SQL. The command is built once per checkpoint, so the text is prepared once as before.
    internal static string WithCheckpointLiterals(string sql, WorldId worldId, string writeUtc) =>
        sql.Replace("$worldId", worldId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$updatedUtc", "'" + writeUtc.Replace("'", "''", StringComparison.Ordinal) + "'", StringComparison.Ordinal);
}
