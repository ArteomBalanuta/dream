namespace Dream.Server.Persistence.Migrations;

// The database was migrated by a newer build than this one. The migrator leaves the file untouched.
public sealed class SchemaTooNewException(int stored, int latest) : InvalidOperationException(
    $"The database schema is at version {stored}, but this build only knows migrations up to version {latest}. " +
    "Run a newer build, or restore a backup made before the upgrade.")
{
    public int StoredVersion { get; } = stored;

    public int LatestVersion { get; } = latest;
}
