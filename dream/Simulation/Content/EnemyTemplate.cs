namespace Dream.Simulation.Content;

// How the population step uses an enemy kind (wave I). Primary kinds fill the one daytime slot (and
// the first night slot); NightExtra kinds fill the second night slot. A catalog with no NightExtra
// spawns only Primary kinds, as before.
public enum EnemySpawnRole : byte
{
    Primary = 0,
    NightExtra = 1,
}

// An enemy kind as authored in enemies.json. Enemy rows reference it by Key (enemy_kind), with no
// foreign key. The body (capsule, anchor, stand-off) stays in Simulation.Enemies.WolfDefinition.
//
// Strength, Vitality, AttackSpeed and Level are the stats a spawn writes into its row (an enemy's
// stats are instance stats, like a rolled item's); damage, maximum health and the attack interval
// come from the enemy.* formulas. AttackIntervalSeconds, when set, replaces the interval formula
// for new spawns. VisualKey names the client's model and tint; LootTable names a loot.json table.
public sealed record EnemyTemplate(
    string Key,
    string DisplayName,
    int Level,
    int Strength,
    int Vitality,
    float AttackSpeed,
    float? AttackIntervalSeconds,
    float AggroRange,
    float AttackRange,
    float MoveSpeed,
    string VisualKey,
    string? LootTable,
    EnemySpawnRole SpawnRole);

// A spawn camp as authored in spawns.json (M2-B): Count enemies of Template stand around (X, Z) on the
// floor; a dead one is replaced RespawnSeconds later (counted in simulation ticks). A Night camp only
// exists while it is night: its enemies despawn at dawn and respawn only in the dark. Enemy rows keep
// the camp by Key (camp_key), with no foreign key.
public sealed record SpawnCamp(
    string Key,
    string Template,
    float X,
    float Z,
    int Count,
    float RespawnSeconds,
    bool Night);
