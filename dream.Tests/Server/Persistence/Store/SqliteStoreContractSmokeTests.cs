using Dream.Server.Persistence;
using Dream.Server.Persistence.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Store;

// Wave A contract (C0): the store's session semantics. A2 extends this into SqliteStoreTests.
public sealed class SqliteStoreContractSmokeTests
{
    [Fact]
    public void SessionsInsideABatch_EnlistOnTheBatchTransaction_AndCommitOnce()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);
        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            batch.BeginWrite();
            for (int i = 0; i < 3; i++)
            {
                using SqliteSession session = store.OpenSession();
                Assert.True(session.IsEnlisted);
                Assert.Null(session.Transaction);
                // Fails with "Execute requires the command to have a transaction object" unless the
                // command is bound to the batch's pending transaction.
                InsertMetadata(session, $"enlisted-{i}");
            }

            batch.CompleteWrite();
            // Nothing is visible to another connection until the batch commits.
            Assert.Equal(0L, CountEnlistedKeys(database));
            batch.Commit();
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }

        Assert.Equal(1L, batch.CommittedTransactions);
        Assert.Equal(3L, CountEnlistedKeys(database));
    }

    [Fact]
    public void OutsideABatch_OpenSession_OwnsAnAutocommitConnection()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);

        using (SqliteSession session = store.OpenSession())
        {
            Assert.False(session.IsEnlisted);
            Assert.Null(session.Transaction);
            InsertMetadata(session, "enlisted-autocommit");
        }

        Assert.Equal(1L, CountEnlistedKeys(database));
    }

    [Fact]
    public void OwnedTransaction_CommitsOnCommit_AndRollsBackOnDisposeWithoutCommit()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);

        using (SqliteSession session = store.BeginOwnedTransaction())
        {
            Assert.False(session.IsEnlisted);
            Assert.NotNull(session.Transaction);
            InsertMetadata(session, "enlisted-committed");
            session.Commit();
        }

        using (SqliteSession session = store.BeginOwnedTransaction())
        {
            InsertMetadata(session, "enlisted-rolled-back");
        }

        Assert.Equal(1L, database.Count("SELECT COUNT(*) FROM world_metadata WHERE key = 'enlisted-committed';"));
        Assert.Equal(0L, database.Count("SELECT COUNT(*) FROM world_metadata WHERE key = 'enlisted-rolled-back';"));
    }

    [Fact]
    public void OwnedTransaction_NeverEnlists_EvenInsideABatch()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);
        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            using SqliteSession session = store.BeginOwnedTransaction();
            Assert.False(session.IsEnlisted);
            InsertMetadata(session, "enlisted-owned");
            session.Commit();
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }

        Assert.Equal(0L, batch.CommittedTransactions);
        Assert.Equal(1L, CountEnlistedKeys(database));
    }

    [Fact]
    public void Commit_OnAnEnlistedSession_Throws_AndLeavesTheBatchToCommit()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);
        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            batch.BeginWrite();
            using (SqliteSession session = store.OpenSession())
            {
                InsertMetadata(session, "enlisted-batch");
                Assert.Throws<InvalidOperationException>(session.Commit);
            }

            batch.CompleteWrite();
            batch.Commit();
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }

        Assert.Equal(1L, batch.CommittedTransactions);
        Assert.Equal(1L, CountEnlistedKeys(database));
    }

    [Fact]
    public void Commit_OnAnAutocommitSession_Throws()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);

        using SqliteSession session = store.OpenSession();
        Assert.Throws<InvalidOperationException>(session.Commit);
    }

    private static void InsertMetadata(SqliteSession session, string key)
    {
        using SqliteCommand command = session.CreateCommand(
            "INSERT INTO world_metadata (key, value) VALUES ($key, $value);");
        command.BindText("$key", key);
        command.BindText("$value", "1");
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static long CountEnlistedKeys(PersistenceTestDatabase database)
    {
        return database.Count("SELECT COUNT(*) FROM world_metadata WHERE key LIKE 'enlisted-%';");
    }
}
