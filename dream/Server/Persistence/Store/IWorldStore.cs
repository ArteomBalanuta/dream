namespace Dream.Server.Persistence.Store;

// Where repositories get their SQLite sessions. One session per repository call, as one
// SqliteConnectionLease per call today.
public interface IWorldStore
{
    SqliteSchemaBootstrapper Schema { get; }

    // Same semantics as SqliteSchemaBootstrapper.OpenConnection: inside an active SqliteWriteBatch on
    // this thread the session runs on the batch's connection, transaction and current savepoint
    // (IsEnlisted); otherwise it owns a fresh autocommit connection. Disposing never commits a batch.
    SqliteSession OpenSession();

    // Never enlists: an owned connection plus connection.BeginTransaction() (IMMEDIATE, as today's
    // checkpoint writer). Commit() commits; Dispose without Commit rolls back.
    SqliteSession BeginOwnedTransaction();
}
