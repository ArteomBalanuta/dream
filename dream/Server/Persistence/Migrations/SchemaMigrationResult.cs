namespace Dream.Server.Persistence.Migrations;

// What ISchemaMigrator.Migrate did: FromVersion == ToVersion when nothing was pending. BackupPath is
// null when no backup was made (a new database, or nothing pending).
public sealed record SchemaMigrationResult(int FromVersion, int ToVersion, string? BackupPath);
