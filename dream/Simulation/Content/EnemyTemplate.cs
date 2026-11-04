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
// MaxHealth (the optional "maxHealth") replaces the enemy.max_health formula for new spawns: vitality is capped, so it is how
// a high tier gets more health. A restored row keeps the health it was saved with.
// GoldMin..GoldMax (the optional "goldMin" and "goldMax") is the gold a kill pays, inclusive; 0 and 0 pays none.
//
// MoveSpeed is THE CHASE SPEED: what the enemy runs at while it is closing on a player. The optional
// "patrolSpeed" is the slower walk it returns home at; absent, it is half the chase speed. The authored
// value is kept nullable so content without it keeps its digest, exactly as "maxHealth" does.
//
// The shipped speeds are set by one relative constraint, not by real animal figures (a real wolf gallops
// 10-16 m/s, which is unreadable in an isometric camera): a chasing enemy outruns a RUNNING player
// (4.3 m/s) so sprint (6.0) is the escape tool, except the top tier, which sprint can only hold level
// with. enemies.json carries no comment of its own because the loader rejects JSON comments, so the
// table lives here:
//
//   enemy          patrolSpeed  moveSpeed (chase)  was
//   Wolf           1.4          5.0                2.8
//   DireWolf       1.6          5.6                3.2
//   AshenProwler   1.7          5.8                3.4
//   GraveHowler    1.8          6.0                3.6
//
// GraveHowler is 6.0 and deliberately not higher: with no stamina system an enemy that strictly outruns
// sprint on open ground is an unescapable trap, while one that exactly matches sprint holds its distance
// forever without ever closing - the same dread, still beatable with terrain and cubes. The old 2.8-3.6
// band was the actual unrealism: every enemy was slower than the player's single speed, so none of them
// could ever close.
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
    EnemySpawnRole SpawnRole,
    int GoldMin = 0,
    int GoldMax = 0,
    int? MaxHealth = null,
    float? AuthoredPatrolSpeed = null)
{
    // The walk home: the authored "patrolSpeed", or half the chase speed when it is absent.
    public float PatrolSpeed => AuthoredPatrolSpeed ?? MoveSpeed * 0.5f;
}

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
    bool Night,
    int MinWorldSize = 0,
    int MaxWorldSize = 0)
{
    // A camp with a MinWorldSize is authored in metres: X and Z are never scaled to the map.
    public bool IsAbsolute => MinWorldSize > 0;

    // Whether the camp exists on a floor of floorSize metres (the optional minWorldSize..maxWorldSize band).
    public bool ExistsOn(int floorSize) =>
        floorSize >= MinWorldSize && (MaxWorldSize == 0 || floorSize <= MaxWorldSize);
}
