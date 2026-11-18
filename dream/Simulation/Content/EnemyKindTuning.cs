using System.Globalization;
using Dream.Simulation.Enemies;

namespace Dream.Simulation.Content;

// One row of the global enemy_kinds table: the numbers of one enemy kind that a designer tunes in the
// database. They override what enemies.json authors (ContentCatalog.WithEnemyTuning) for new spawns and
// for restored enemies alike. The rules are the ones enemies.json is validated against.
public readonly record struct EnemyKindTuning(
    string EnemyKind,
    float ChaseSpeed,
    float PatrolSpeed,
    float AttackIntervalSeconds,
    float AttackRange,
    float AggroRange)
{
    public const float MinAttackIntervalSeconds = 0.05f;
    public const float MaxAttackIntervalSeconds = 3600f;
    public const float MaxSpeed = 100f;
    public const float MaxRange = 1000f;

    // Whether the row meets the content rules; reason says which one it breaks when it does not.
    public bool IsValid(out string reason)
    {
        if (string.IsNullOrWhiteSpace(EnemyKind))
        {
            reason = "empty enemy kind";
            return false;
        }

        if (!float.IsFinite(ChaseSpeed) || ChaseSpeed < ContentCatalog.MinMoveSpeed || ChaseSpeed > MaxSpeed)
        {
            reason = $"chase_speed {Format(ChaseSpeed)} is outside [{Format(ContentCatalog.MinMoveSpeed)}, {Format(MaxSpeed)}]";
            return false;
        }

        if (!float.IsFinite(PatrolSpeed) || PatrolSpeed < ContentCatalog.MinMoveSpeed || PatrolSpeed > ChaseSpeed)
        {
            reason = $"patrol_speed {Format(PatrolSpeed)} is outside [{Format(ContentCatalog.MinMoveSpeed)}, chase_speed]";
            return false;
        }

        if (!float.IsFinite(AttackIntervalSeconds)
            || AttackIntervalSeconds < MinAttackIntervalSeconds
            || AttackIntervalSeconds > MaxAttackIntervalSeconds)
        {
            reason = $"attack_interval_seconds {Format(AttackIntervalSeconds)} is outside [{Format(MinAttackIntervalSeconds)}, {Format(MaxAttackIntervalSeconds)}]";
            return false;
        }

        // The same bound ParseEnemies applies: a range at or below this stops the enemy inside the
        // player separation, where it shoves instead of biting.
        float minimumAttackRange = WolfDefinition.PlayerSeparationDistance / EnemyActor.StandOffRangeFraction;
        if (!float.IsFinite(AttackRange) || AttackRange <= minimumAttackRange || AttackRange > MaxRange)
        {
            reason = $"attack_range {Format(AttackRange)} must exceed {Format(minimumAttackRange)} and stay within {Format(MaxRange)}";
            return false;
        }

        if (!float.IsFinite(AggroRange) || AggroRange < 0.1f || AggroRange > MaxRange)
        {
            reason = $"aggro_range {Format(AggroRange)} is outside [0.1, {Format(MaxRange)}]";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
