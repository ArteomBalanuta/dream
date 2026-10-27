using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// Writes a whole checkpoint on one connection inside one transaction, reusing one prepared
// command per table. The per-row repository methods each open a connection and autocommit,
// which stalled the server tick for about 0.5 s every checkpoint.
public sealed class SqlitePersistenceCheckpointWriter : IPersistenceCheckpointWriter
{
    private readonly SqliteSchemaBootstrapper _schema;

    public SqlitePersistenceCheckpointWriter(SqliteSchemaBootstrapper schema)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public void Write(PersistenceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        string updatedUtc = SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow);
        using SqliteConnection connection = _schema.CreateOpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        WritePlayerVitals(connection, transaction, checkpoint.PlayerVitals, updatedUtc);
        WritePlayerWorldStates(connection, transaction, checkpoint.PlayerWorldStates, updatedUtc);
        WriteEnemies(connection, transaction, checkpoint.Enemies, updatedUtc);
        WritePlacedCubes(connection, transaction, checkpoint.PlacedCubes, updatedUtc);
        WriteFracturePieces(connection, transaction, checkpoint.FracturePieces, updatedUtc);

        transaction.Commit();
    }

    private static void WritePlayerVitals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PlayerCheckpointVitals> vitals,
        string updatedUtc)
    {
        if (vitals.Count == 0)
        {
            return;
        }

        using SqliteCommand command = CreateCommand(connection, transaction, """
            UPDATE player_profiles SET
                interaction_mode = $interactionMode,
                current_hp = $currentHp,
                max_hp = $maxHp,
                updated_utc = $updatedUtc
            WHERE player_name = $playerName;
            """);
        SqliteParameter playerName = command.Parameters.Add("$playerName", SqliteType.Text);
        SqliteParameter interactionMode = command.Parameters.Add("$interactionMode", SqliteType.Text);
        SqliteParameter currentHp = command.Parameters.Add("$currentHp", SqliteType.Integer);
        SqliteParameter maxHp = command.Parameters.Add("$maxHp", SqliteType.Integer);
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc);

        foreach (PlayerCheckpointVitals entry in vitals)
        {
            playerName.Value = entry.PlayerName.Trim();
            interactionMode.Value = entry.InteractionMode.ToString();
            currentHp.Value = entry.CurrentHealth;
            maxHp.Value = entry.MaxHealth;
            command.ExecuteNonQuery();
        }
    }

    private static void WritePlayerWorldStates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PersistedPlayerWorldState> states,
        string updatedUtc)
    {
        if (states.Count == 0)
        {
            return;
        }

        // Upsert is safe here: only the checkpoint writes player_world_state.
        using SqliteCommand command = CreateCommand(connection, transaction, """
            INSERT INTO player_world_state (
                player_name, position_x, position_y, position_z,
                facing_x, facing_y, facing_z, current_hp, current_energy, updated_utc)
            SELECT
                $playerName, $positionX, $positionY, $positionZ,
                $facingX, $facingY, $facingZ, $currentHp, $currentEnergy, $updatedUtc
            WHERE EXISTS (SELECT 1 FROM player_profiles WHERE player_name = $playerName)
            ON CONFLICT(player_name) DO UPDATE SET
                position_x = excluded.position_x,
                position_y = excluded.position_y,
                position_z = excluded.position_z,
                facing_x = excluded.facing_x,
                facing_y = excluded.facing_y,
                facing_z = excluded.facing_z,
                current_hp = excluded.current_hp,
                current_energy = excluded.current_energy,
                updated_utc = excluded.updated_utc;
            """);
        SqliteParameter playerName = command.Parameters.Add("$playerName", SqliteType.Text);
        SqliteParameter positionX = command.Parameters.Add("$positionX", SqliteType.Real);
        SqliteParameter positionY = command.Parameters.Add("$positionY", SqliteType.Real);
        SqliteParameter positionZ = command.Parameters.Add("$positionZ", SqliteType.Real);
        SqliteParameter facingX = command.Parameters.Add("$facingX", SqliteType.Real);
        SqliteParameter facingY = command.Parameters.Add("$facingY", SqliteType.Real);
        SqliteParameter facingZ = command.Parameters.Add("$facingZ", SqliteType.Real);
        SqliteParameter currentHp = command.Parameters.Add("$currentHp", SqliteType.Integer);
        SqliteParameter currentEnergy = command.Parameters.Add("$currentEnergy", SqliteType.Integer);
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc);

        foreach (PersistedPlayerWorldState state in states)
        {
            playerName.Value = state.PlayerName.Trim();
            positionX.Value = state.Position.X;
            positionY.Value = state.Position.Y;
            positionZ.Value = state.Position.Z;
            facingX.Value = state.Facing.X;
            facingY.Value = state.Facing.Y;
            facingZ.Value = state.Facing.Z;
            currentHp.Value = state.CurrentHealth;
            currentEnergy.Value = state.CurrentEnergy;
            command.ExecuteNonQuery();
        }
    }

    private static void WriteEnemies(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PersistedEnemyInstance> enemies,
        string updatedUtc)
    {
        if (enemies.Count == 0)
        {
            return;
        }

        using SqliteCommand command = CreateCommand(connection, transaction, """
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
            WHERE enemy_id = $enemyId;
            """);
        SqliteParameter enemyId = command.Parameters.Add("$enemyId", SqliteType.Integer);
        SqliteParameter positionX = command.Parameters.Add("$positionX", SqliteType.Real);
        SqliteParameter positionY = command.Parameters.Add("$positionY", SqliteType.Real);
        SqliteParameter positionZ = command.Parameters.Add("$positionZ", SqliteType.Real);
        SqliteParameter facingX = command.Parameters.Add("$facingX", SqliteType.Real);
        SqliteParameter facingY = command.Parameters.Add("$facingY", SqliteType.Real);
        SqliteParameter facingZ = command.Parameters.Add("$facingZ", SqliteType.Real);
        SqliteParameter currentHp = command.Parameters.Add("$currentHp", SqliteType.Integer);
        SqliteParameter maxHp = command.Parameters.Add("$maxHp", SqliteType.Integer);
        SqliteParameter level = command.Parameters.Add("$level", SqliteType.Integer);
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc);

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

    private static void WritePlacedCubes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PersistedPlacedCube> cubes,
        string updatedUtc)
    {
        if (cubes.Count == 0)
        {
            return;
        }

        using SqliteCommand command = CreateCommand(connection, transaction, """
            UPDATE placed_cube_instances SET
                cell_x = $cellX,
                cell_y = $cellY,
                cell_z = $cellZ,
                center_x = $centerX,
                center_y = $centerY,
                center_z = $centerZ,
                material_id = $materialId,
                current_health = $currentHealth,
                max_health = $maxHealth,
                updated_utc = $updatedUtc
            WHERE cube_id = $cubeId;
            """);
        SqliteParameter cubeId = command.Parameters.Add("$cubeId", SqliteType.Integer);
        SqliteParameter cellX = command.Parameters.Add("$cellX", SqliteType.Real);
        SqliteParameter cellY = command.Parameters.Add("$cellY", SqliteType.Real);
        SqliteParameter cellZ = command.Parameters.Add("$cellZ", SqliteType.Real);
        SqliteParameter centerX = command.Parameters.Add("$centerX", SqliteType.Real);
        SqliteParameter centerY = command.Parameters.Add("$centerY", SqliteType.Real);
        SqliteParameter centerZ = command.Parameters.Add("$centerZ", SqliteType.Real);
        SqliteParameter materialId = command.Parameters.Add("$materialId", SqliteType.Text);
        SqliteParameter currentHealth = command.Parameters.Add("$currentHealth", SqliteType.Integer);
        SqliteParameter maxHealth = command.Parameters.Add("$maxHealth", SqliteType.Integer);
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc);

        foreach (PersistedPlacedCube cube in cubes)
        {
            cubeId.Value = cube.CubeId;
            cellX.Value = cube.CellPosition.X;
            cellY.Value = cube.CellPosition.Y;
            cellZ.Value = cube.CellPosition.Z;
            centerX.Value = cube.CenterPosition.HasValue ? cube.CenterPosition.Value.X : DBNull.Value;
            centerY.Value = cube.CenterPosition.HasValue ? cube.CenterPosition.Value.Y : DBNull.Value;
            centerZ.Value = cube.CenterPosition.HasValue ? cube.CenterPosition.Value.Z : DBNull.Value;
            materialId.Value = cube.MaterialId.ToString();
            currentHealth.Value = cube.CurrentHealth;
            maxHealth.Value = cube.MaxHealth;
            command.ExecuteNonQuery();
        }
    }

    private static void WriteFracturePieces(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PersistedCubeFracturePiece> pieces,
        string updatedUtc)
    {
        if (pieces.Count == 0)
        {
            return;
        }

        using SqliteCommand command = CreateCommand(connection, transaction, """
            UPDATE cube_fracture_pieces SET
                center_x = $centerX,
                center_y = $centerY,
                center_z = $centerZ,
                side_length = $sideLength,
                updated_utc = $updatedUtc
            WHERE source_cube_id = $sourceCubeId AND piece_index = $pieceIndex;
            """);
        SqliteParameter sourceCubeId = command.Parameters.Add("$sourceCubeId", SqliteType.Integer);
        SqliteParameter pieceIndex = command.Parameters.Add("$pieceIndex", SqliteType.Integer);
        SqliteParameter centerX = command.Parameters.Add("$centerX", SqliteType.Real);
        SqliteParameter centerY = command.Parameters.Add("$centerY", SqliteType.Real);
        SqliteParameter centerZ = command.Parameters.Add("$centerZ", SqliteType.Real);
        SqliteParameter sideLength = command.Parameters.Add("$sideLength", SqliteType.Real);
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc);

        foreach (PersistedCubeFracturePiece piece in pieces)
        {
            sourceCubeId.Value = piece.SourceCubeId;
            pieceIndex.Value = piece.PieceIndex;
            centerX.Value = piece.Center.X;
            centerY.Value = piece.Center.Y;
            centerZ.Value = piece.Center.Z;
            sideLength.Value = piece.SideLength;
            command.ExecuteNonQuery();
        }
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
