using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Store;

// One unit of database work handed out by IWorldStore. Three shapes:
// - enlisted: the ambient SqliteWriteBatch's connection. The batch owns the connection, its
//   transaction and the current write's savepoint; the session never commits or closes them.
// - owned autocommit: a fresh connection the session closes on Dispose.
// - owned transaction: a fresh connection plus an IMMEDIATE transaction; Commit commits, Dispose
//   without Commit rolls back, and Dispose closes the connection.
public sealed class SqliteSession : IDisposable
{
    private bool _committed;
    private bool _disposed;

    private SqliteSession(SqliteConnection connection, SqliteTransaction? transaction, bool isEnlisted)
    {
        Connection = connection;
        Transaction = transaction;
        IsEnlisted = isEnlisted;
    }

    public SqliteConnection Connection { get; }

    // Non-null only for a session from BeginOwnedTransaction. An enlisted session's transaction
    // belongs to the batch and is not exposed.
    public SqliteTransaction? Transaction { get; }

    public bool IsEnlisted { get; }

    internal static SqliteSession Enlisted(SqliteConnection connection) =>
        new(connection, transaction: null, isEnlisted: true);

    internal static SqliteSession OwnedAutocommit(SqliteConnection connection) =>
        new(connection, transaction: null, isEnlisted: false);

    internal static SqliteSession OwnedTransaction(SqliteConnection connection, SqliteTransaction transaction) =>
        new(connection, transaction, isEnlisted: false);

    // SqliteConnection.CreateCommand already binds the connection's pending transaction. For an
    // enlisted session that is the batch's transaction; the batch's savepoint is a statement inside
    // that same transaction, so the command needs nothing more. The owned transaction is also set
    // explicitly.
    public SqliteCommand CreateCommand(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SqliteCommand command = Connection.CreateCommand();
        command.CommandText = sql;
        if (Transaction != null)
        {
            command.Transaction = Transaction;
        }

        return command;
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEnlisted)
        {
            throw new InvalidOperationException(
                "An enlisted session cannot commit: its SqliteWriteBatch commits the whole group.");
        }

        if (Transaction == null)
        {
            throw new InvalidOperationException(
                "This session runs in autocommit mode; only a session from BeginOwnedTransaction can commit.");
        }

        if (_committed)
        {
            throw new InvalidOperationException("The session's transaction was already committed.");
        }

        Transaction.Commit();
        _committed = true;
    }

    // Enlisted: leaves the batch's connection and transaction alone. Owned: rolls back an
    // uncommitted transaction (disposing it does that) and closes the connection.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsEnlisted)
        {
            return;
        }

        try
        {
            Transaction?.Dispose();
        }
        finally
        {
            Connection.Dispose();
        }
    }
}
