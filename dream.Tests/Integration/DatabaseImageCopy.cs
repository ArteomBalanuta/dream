using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Integration;

// Seeding a second database from a first, for the lockstep pairs that run two servers on two copies
// of one image.
//
// Six fixtures used to do this by hand: release the pool, then copy the main file, then -wal, then
// -shm, as separate unlocked syscalls. That is not a point-in-time copy. Whether source-wal still
// existed at that moment depended on whether the last connection had finished closing (closing it
// checkpoints and truncates the WAL), so the main file could be copied and the WAL then
// checkpointed away behind it, leaving the target seeded from an image that predates the source.
// Copying -shm is unsupported at all: it is a shared-memory index valid only for the processes
// mapping it.
//
// A mis-seed does not announce itself - it surfaces hundreds of ticks later as "the servers
// diverged", pointing the reader at lockstep determinism instead of at the fixture. So this folds
// the WAL into the main file, proves nothing of the database is left outside it, copies that one
// file, and proves the copy is byte for byte the same. Every failure here names itself as a fixture
// failure.
internal static class DatabaseImageCopy
{
    public static void Seed(string sourcePath, string targetPath)
    {
        SqlitePools.Release(sourcePath, targetPath);
        Checkpoint(sourcePath);

        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string sidecar = sourcePath + suffix;
            long length = File.Exists(sidecar) ? new FileInfo(sidecar).Length : 0;
            Assert.True(
                length == 0,
                $"fixture: {Path.GetFileName(sidecar)} still holds {length} bytes after the checkpoint, so the copy below would not be a point-in-time image");
            File.Delete(targetPath + suffix);
        }

        File.Copy(sourcePath, targetPath, overwrite: true);
        Assert.Equal(Hash(sourcePath), Hash(targetPath));
    }

    // PRAGMA wal_checkpoint(TRUNCATE) on a connection of its own, so the checkpoint is not left to
    // the timing of the last pooled connection's close. Pooling=False, so disposing it really
    // closes it and the file is free afterwards.
    private static void Checkpoint(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    private static string Hash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
