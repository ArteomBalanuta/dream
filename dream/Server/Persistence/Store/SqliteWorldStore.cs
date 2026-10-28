using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Store;

public sealed class SqliteWorldStore(SqliteSchemaBootstrapper schema) : IWorldStore
{
    public SqliteSchemaBootstrapper Schema { get; } = schema;

    public SqliteSession OpenSession()
    {
        SqliteConnection? enlisted = SqliteWriteBatch.TryEnlist(Schema);
        return enlisted != null
            ? SqliteSession.Enlisted(enlisted)
            : SqliteSession.OwnedAutocommit(Schema.CreateOpenConnection());
    }

    public SqliteSession BeginOwnedTransaction()
    {
        SqliteConnection connection = Schema.CreateOpenConnection();
        try
        {
            // deferred: false (IMMEDIATE) is BeginTransaction()'s default; spelled out because the
            // write lock must be taken up front, as the checkpoint writer does today.
            SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            return SqliteSession.OwnedTransaction(connection, transaction);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
