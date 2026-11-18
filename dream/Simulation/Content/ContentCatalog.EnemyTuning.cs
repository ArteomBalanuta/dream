using Dream.Simulation.Players;

namespace Dream.Simulation.Content;

// The database's per-kind enemy tuning (enemy_kinds), laid over the authored catalog.
public sealed partial class ContentCatalog
{
    // A catalog equal to this one except that every enemy kind named in tuning uses the row's chase
    // speed, patrol speed, attack interval, attack range and aggro range. Kinds with no row, and rows
    // for kinds the content does not have, change nothing. ContentDigest is kept: it covers the
    // authored files, and the server logs the tuning separately. Rows must already be valid
    // (EnemyKindTuning.IsValid); an invalid one throws.
    public ContentCatalog WithEnemyTuning(IReadOnlyList<EnemyKindTuning> tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        Dictionary<string, EnemyKindTuning> byKind = new(StringComparer.Ordinal);
        foreach (EnemyKindTuning row in tuning)
        {
            if (!row.IsValid(out string reason))
            {
                throw new ArgumentException($"enemy kind '{row.EnemyKind}': {reason}", nameof(tuning));
            }

            byKind[row.EnemyKind] = row;
        }

        Dictionary<string, EnemyTemplate> enemies = new(_enemies.Count, StringComparer.Ordinal);
        List<EnemyTemplate> list = new(Enemies.Count);
        foreach (EnemyTemplate template in Enemies)
        {
            EnemyTemplate tuned = byKind.TryGetValue(template.Key, out EnemyKindTuning row)
                ? template with
                {
                    MoveSpeed = row.ChaseSpeed,
                    AuthoredPatrolSpeed = row.PatrolSpeed,
                    AttackIntervalSeconds = row.AttackIntervalSeconds,
                    AttackRange = row.AttackRange,
                    AggroRange = row.AggroRange,
                    // The sheet stat follows the interval so the row a spawn writes agrees with it.
                    AttackSpeed = Math.Clamp(1f / row.AttackIntervalSeconds, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed),
                }
                : template;
            enemies[tuned.Key] = tuned;
            list.Add(tuned);
        }

        ContentCatalog catalog = new(
            _items,
            Items,
            StarterKit,
            StarterKitVersion,
            UnrecordedStarterKitVersion,
            enemies,
            list,
            _lootTables,
            LootTables,
            Camps,
            Npcs,
            _skills,
            Skills,
            DefaultQuickbar,
            ContentDigest);
        catalog.SetQuests([.. Quests]);
        return catalog;
    }
}
