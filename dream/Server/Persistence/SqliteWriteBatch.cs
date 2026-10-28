using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence;

// A connection handed out by SqliteSchemaBootstrapper.OpenConnection, which only tests and tools use
// now (repositories take SqliteSession from IWorldStore.OpenSession). Disposing it closes the
// connection only when the lease opened it; a connection enlisted in a SqliteWriteBatch stays open
// for the batch's next write.
public readonly struct SqliteConnectionLease : IDisposable
{
    private readonly bool _owned;

    internal SqliteConnectionLease(SqliteConnection connection, bool owned)
    {
        Connection = connection;
        _owned = owned;
    }

    public SqliteConnection Connection { get; }

    public void Dispose()
    {
        if (_owned)
        {
            Connection.Dispose();
        }
    }
}

// Group commit for PersistenceWriteQueue's worker. While a batch is active on a thread, every
// repository call on that thread runs on one open connection per database inside one write
// transaction, and each queued write runs inside its own savepoint: a failing write is rolled back
// to its savepoint without undoing the writes before or after it, and the whole group costs one
// commit (one WAL sync point) instead of one autocommit per write. Repositories need no batch
// awareness: IWorldStore.OpenSession enlists their sessions through TryEnlist.
//
// Not thread-safe: one worker thread owns a batch, and the ambient slot is per thread.
internal sealed class SqliteWriteBatch : IPersistenceWriteBatch
{
    private const string SavepointName = "dream_write";

    // PersistenceWriteQueue (Simulation) only knows the IPersistenceWriteBatch port; this installs
    // the SQLite batch as its default when the assembly loads, before any queue can start a worker.
#pragma warning disable CA2255 // The initializer only sets a factory default inside this same assembly, and runs before any PersistenceWriteQueue exists.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RegisterAsDefault()
    {
        PersistenceWriteQueue.WriteBatchFactory = static () => new SqliteWriteBatch();
    }

    [ThreadStatic]
    private static SqliteWriteBatch? t_current;

    private readonly List<Enlisted> _enlisted = [];
    private bool _inWrite;

    // Transactions committed by Commit, for tests and diagnostics.
    public long CommittedTransactions { get; private set; }

    // Set when a connection could not be opened or its transaction could not start (the database
    // stayed locked past busy_timeout). The queue then fails the rest of the group instead of
    // waiting busy_timeout again for every write. Cleared by Commit and Abandon.
    public bool TransactionStartFailed { get; private set; }

    // Set with TransactionStartFailed when the cause was SQLITE_BUSY or SQLITE_LOCKED: another
    // connection (the checkpoint writer, a DB browser, a backup) held the write lock. That is
    // transient, so the queue puts the unrun writes back and retries instead of dropping them.
    // Cleared by Commit and Abandon.
    public bool TransactionStartBusy { get; private set; }

    public static SqliteWriteBatch? Current => t_current;

    // Makes this batch the ambient one on the calling thread until Deactivate.
    public void Activate()
    {
        t_current = this;
    }

    public static void Deactivate()
    {
        t_current = null;
    }

    void IPersistenceWriteBatch.Deactivate() => Deactivate();

    // The ambient batch's connection for this schema's database, opened and enlisted in the batch's
    // transaction on first use; null when no batch is active on this thread.
    public static SqliteConnection? TryEnlist(SqliteSchemaBootstrapper schema)
    {
        return t_current?.Enlist(schema);
    }

    // Starts one queued write: a savepoint on every connection already in the transaction.
    // Connections enlisted during the write get theirs when they enlist.
    public void BeginWrite()
    {
        _inWrite = true;
        for (int i = 0; i < _enlisted.Count; i++)
        {
            OpenSavepoint(_enlisted[i]);
        }
    }

    // Keeps the write's changes in the transaction.
    public void CompleteWrite()
    {
        _inWrite = false;
        for (int i = 0; i < _enlisted.Count; i++)
        {
            Enlisted entry = _enlisted[i];
            if (entry.SavepointOpen)
            {
                entry.Transaction!.Release(SavepointName);
                entry.SavepointOpen = false;
            }
        }
    }

    // Undoes only the write's own changes. Throws when the connection cannot roll back (the
    // transaction itself is gone); the caller then abandons the whole batch.
    public void RollBackWrite()
    {
        _inWrite = false;
        for (int i = 0; i < _enlisted.Count; i++)
        {
            Enlisted entry = _enlisted[i];
            if (entry.SavepointOpen)
            {
                entry.SavepointOpen = false;
                entry.Transaction!.Rollback(SavepointName);
                entry.Transaction.Release(SavepointName);
            }
        }
    }

    // Commits every open transaction. The connections stay open for the next group.
    public void Commit()
    {
        TransactionStartFailed = false;
        TransactionStartBusy = false;
        for (int i = 0; i < _enlisted.Count; i++)
        {
            Enlisted entry = _enlisted[i];
            if (entry.Transaction == null)
            {
                continue;
            }

            SqliteTransaction transaction = entry.Transaction;
            entry.Transaction = null;
            entry.SavepointOpen = false;
            using (transaction)
            {
                transaction.Commit();
            }

            CommittedTransactions++;
        }
    }

    // Rolls back whatever is open and closes the connections; the next Enlist reopens them.
    public void Abandon()
    {
        _inWrite = false;
        TransactionStartFailed = false;
        TransactionStartBusy = false;
        for (int i = 0; i < _enlisted.Count; i++)
        {
            Enlisted entry = _enlisted[i];
            try
            {
                // Disposing an uncommitted transaction rolls it back.
                entry.Transaction?.Dispose();
            }
            catch (Exception)
            {
                // The connection is closed next, which ends the transaction anyway.
            }

            try
            {
                entry.Connection.Dispose();
            }
            catch (Exception)
            {
                // Nothing more to release; the next Enlist opens a fresh connection.
            }
        }

        _enlisted.Clear();
    }

    public void Dispose()
    {
        if (ReferenceEquals(t_current, this))
        {
            t_current = null;
        }

        Abandon();
    }

    private SqliteConnection Enlist(SqliteSchemaBootstrapper schema)
    {
        Enlisted? entry = null;
        for (int i = 0; i < _enlisted.Count; i++)
        {
            if (string.Equals(_enlisted[i].ConnectionString, schema.ConnectionString, StringComparison.Ordinal))
            {
                entry = _enlisted[i];
                break;
            }
        }

        try
        {
            if (entry == null)
            {
                entry = new Enlisted(schema.ConnectionString, schema.CreateOpenConnection());
                _enlisted.Add(entry);
            }

            if (entry.Transaction == null)
            {
                // IMMEDIATE: take the write lock now (waiting out another writer through busy_timeout),
                // so a write after a read in the same group can never fail with a stale snapshot.
                entry.Transaction = entry.Connection.BeginTransaction(deferred: false);
            }
        }
        catch (Exception ex)
        {
            TransactionStartFailed = true;
            TransactionStartBusy = IsBusy(ex);
            throw;
        }

        if (_inWrite)
        {
            OpenSavepoint(entry);
        }

        return entry.Connection;
    }

    // SQLITE_BUSY (5) or SQLITE_LOCKED (6), including extended codes such as SQLITE_BUSY_SNAPSHOT.
    internal static bool IsBusy(Exception ex)
    {
        return ex is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xFF) is 5 or 6;
    }

    private static void OpenSavepoint(Enlisted entry)
    {
        if (entry.Transaction == null || entry.SavepointOpen)
        {
            return;
        }

        entry.Transaction.Save(SavepointName);
        entry.SavepointOpen = true;
    }

    private sealed class Enlisted(string connectionString, SqliteConnection connection)
    {
        public string ConnectionString { get; } = connectionString;

        public SqliteConnection Connection { get; } = connection;

        public SqliteTransaction? Transaction { get; set; }

        public bool SavepointOpen { get; set; }
    }
}
