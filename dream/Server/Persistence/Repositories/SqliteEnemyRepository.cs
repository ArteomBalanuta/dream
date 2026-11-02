using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Persistence.Repositories;

// enemy_instances: the live enemies, keyed by object_id.
public sealed partial class SqliteEnemyRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IEnemyRepository
{
    // World 1: the default world.
    public SqliteEnemyRepository(IWorldStore store)
        : this(store, WorldId.Default)
    {
    }

    private const string LoadSql = """
        SELECT
            object_id,
            enemy_kind,
            display_name,
            position_x,
            position_y,
            position_z,
            facing_x,
            facing_y,
            facing_z,
            current_hp,
            max_hp,
            strength,
            agility,
            vitality,
            energy,
            movement_speed,
            attack_speed,
            level,
            created_utc,
            updated_utc,
            camp_key
        FROM enemy_instances
        WHERE world_id = $worldId
        ORDER BY object_id;
        """;

    private const string UpsertSql = """
        INSERT INTO enemy_instances (
            world_id,
            object_id,
            enemy_kind,
            display_name,
            position_x,
            position_y,
            position_z,
            facing_x,
            facing_y,
            facing_z,
            current_hp,
            max_hp,
            strength,
            agility,
            vitality,
            energy,
            movement_speed,
            attack_speed,
            level,
            created_utc,
            updated_utc,
            camp_key
        )
        VALUES (
            $worldId,
            $objectId,
            $enemyKind,
            $displayName,
            $positionX,
            $positionY,
            $positionZ,
            $facingX,
            $facingY,
            $facingZ,
            $currentHealth,
            $maxHealth,
            $strength,
            $agility,
            $vitality,
            $energy,
            $movementSpeed,
            $attackSpeed,
            $level,
            $createdUtc,
            $updatedUtc,
            $campKey
        )
        ON CONFLICT(world_id, object_id) DO UPDATE SET
            enemy_kind = excluded.enemy_kind,
            display_name = excluded.display_name,
            position_x = excluded.position_x,
            position_y = excluded.position_y,
            position_z = excluded.position_z,
            facing_x = excluded.facing_x,
            facing_y = excluded.facing_y,
            facing_z = excluded.facing_z,
            current_hp = excluded.current_hp,
            max_hp = excluded.max_hp,
            strength = excluded.strength,
            agility = excluded.agility,
            vitality = excluded.vitality,
            energy = excluded.energy,
            movement_speed = excluded.movement_speed,
            attack_speed = excluded.attack_speed,
            level = excluded.level,
            camp_key = excluded.camp_key,
            updated_utc = excluded.updated_utc;
        """;

    private const string DeleteSql = "DELETE FROM enemy_instances WHERE world_id = $worldId AND object_id = $objectId;";


    public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() =>
        QueryList(LoadSql, static reader => new PersistedEnemyInstance(
            new ObjectId(reader.GetInt64(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetVector3(3),
            reader.GetVector3(6),
            new PersistedCreatureStats(
                CurrentHealth: reader.GetInt32(9),
                MaxHealth: reader.GetInt32(10),
                Strength: reader.GetInt32(11),
                Agility: reader.GetInt32(12),
                Vitality: reader.GetInt32(13),
                Energy: reader.GetInt32(14),
                MovementSpeed: reader.GetFloat(15),
                AttackSpeed: reader.GetFloat(16),
                Level: reader.GetInt32(17)),
            reader.GetUtc(18),
            reader.GetUtc(19),
            reader.IsDBNull(20) ? null : reader.GetString(20)));

    // created_utc keeps enemy.CreatedUtc (UtcNow when unset); updated_utc is the write time.
    public void SaveEnemyInstance(PersistedEnemyInstance enemy) =>
        Execute(UpsertSql, enemy, static (command, e) =>
        {
            command.BindInt64("$objectId", e.ObjectId.Value);
            command.BindText("$enemyKind", e.EnemyKind);
            command.BindText("$displayName", e.DisplayName);
            command.BindVector3("$positionX", "$positionY", "$positionZ", e.Position);
            command.BindVector3("$facingX", "$facingY", "$facingZ", e.Facing);
            command.BindInt64("$currentHealth", e.Stats.CurrentHealth);
            command.BindInt64("$maxHealth", e.Stats.MaxHealth);
            command.BindInt64("$strength", e.Stats.Strength);
            command.BindInt64("$agility", e.Stats.Agility);
            command.BindInt64("$vitality", e.Stats.Vitality);
            command.BindInt64("$energy", e.Stats.Energy);
            command.BindReal("$movementSpeed", e.Stats.MovementSpeed);
            command.BindReal("$attackSpeed", e.Stats.AttackSpeed);
            command.BindInt64("$level", e.Stats.Level);
            command.BindUtc("$createdUtc", e.CreatedUtc == default ? DateTime.UtcNow : e.CreatedUtc);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
            command.BindNullableText("$campKey", e.CampKey);
        });

    public void DeleteEnemyInstance(ObjectId objectId) =>
        Execute(DeleteSql, objectId, static (command, id) => command.BindInt64("$objectId", id.Value));
}
