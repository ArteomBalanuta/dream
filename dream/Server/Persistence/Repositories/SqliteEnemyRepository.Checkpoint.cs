using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// The checkpoint write of enemy_instances, run by SqlitePersistenceCheckpointWriter inside its
// owned transaction.
public sealed partial class SqliteEnemyRepository
{
    // Update only: the write may land after the queue deleted the enemy, which must stay deleted.
    internal const string CheckpointUpdateSql = """
        UPDATE enemy_instances SET
            position_x = $positionX,
            position_y = $positionY,
            position_z = $positionZ,
            facing_x = $facingX,
            facing_y = $facingY,
            facing_z = $facingZ,
            current_hp = $currentHp,
            max_hp = $maxHp,
            level = $level,
            updated_utc = $updatedUtc
        WHERE world_id = $worldId AND enemy_id = $enemyId;
        """;

    // One prepared command for all rows, its parameters set per row. writeUtc is the formatted
    // write time every row is stamped with.
    internal static void CheckpointUpdate(SqliteSession session, WorldId worldId, IReadOnlyList<PersistedEnemyInstance> enemies, string writeUtc)
    {
        if (enemies.Count == 0)
        {
            return;
        }

        using SqliteCommand command = session.CreateCommand(SqliteRepositorySql.WithCheckpointLiterals(CheckpointUpdateSql, worldId, writeUtc));
        SqliteParameter enemyId = command.AddParameter("$enemyId", SqliteType.Integer);
        SqliteParameter positionX = command.AddParameter("$positionX", SqliteType.Real);
        SqliteParameter positionY = command.AddParameter("$positionY", SqliteType.Real);
        SqliteParameter positionZ = command.AddParameter("$positionZ", SqliteType.Real);
        SqliteParameter facingX = command.AddParameter("$facingX", SqliteType.Real);
        SqliteParameter facingY = command.AddParameter("$facingY", SqliteType.Real);
        SqliteParameter facingZ = command.AddParameter("$facingZ", SqliteType.Real);
        SqliteParameter currentHp = command.AddParameter("$currentHp", SqliteType.Integer);
        SqliteParameter maxHp = command.AddParameter("$maxHp", SqliteType.Integer);
        SqliteParameter level = command.AddParameter("$level", SqliteType.Integer);

        foreach (PersistedEnemyInstance enemy in enemies)
        {
            enemyId.Value = enemy.EnemyId;
            positionX.Value = enemy.Position.X;
            positionY.Value = enemy.Position.Y;
            positionZ.Value = enemy.Position.Z;
            facingX.Value = enemy.Facing.X;
            facingY.Value = enemy.Facing.Y;
            facingZ.Value = enemy.Facing.Z;
            currentHp.Value = enemy.Stats.CurrentHealth;
            maxHp.Value = enemy.Stats.MaxHealth;
            level.Value = enemy.Stats.Level;
            command.ExecuteNonQuery();
        }
    }
}
