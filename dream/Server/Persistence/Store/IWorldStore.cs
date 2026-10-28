namespace Dream.Server.Persistence.Store;

// Where repositories get their SQLite sessions: one session per repository call.
public interface IWorldStore
{
    SqliteSchemaBootstrapper Schema { get; }

    // Inside an active SqliteWriteBatch on this thread (SqliteWriteBatch.TryEnlist) the session runs
    // on the batch's connection, transaction and current savepoint (IsEnlisted); otherwise it owns a
    // fresh autocommit connection. Disposing never commits a batch.
    SqliteSession OpenSession();

    // Never enlists: an owned connection plus connection.BeginTransaction() (IMMEDIATE, so the write
    // lock is taken up front); SqlitePersistenceCheckpointWriter uses it. Commit() commits; Dispose
    // without Commit rolls back.
    SqliteSession BeginOwnedTransaction();
}
