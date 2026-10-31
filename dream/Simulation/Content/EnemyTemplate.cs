namespace Dream.Simulation.Content;

// An enemy kind as authored in enemies.json. Enemy rows reference it by Key (enemy_kind), with no
// foreign key. The body (capsule, anchor, stand-off) stays in Simulation.Enemies.WolfDefinition.
public sealed record EnemyTemplate(
    string Key,
    string DisplayName,
    int Level,
    int MaxHealth,
    int AttackDamage,
    float AttackIntervalSeconds,
    float AggroRange,
    float AttackRange,
    float MoveSpeed);
