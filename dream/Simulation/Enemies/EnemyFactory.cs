using OpenTK.Mathematics;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Enemies;

// The one place an enemy is built, for a new spawn and for a restore from a row, so the two cannot
// drift. Strength, Vitality, Attack Speed and Level are instance stats (they come from the template at
// spawn and from the row at restore); damage and the attack interval are derived from them through the
// enemy.* formulas, and maximum health is derived at spawn and read from the row at restore. Aggro and
// attack range, the body, the visual and loot come from the template. Allocates (the actor, its
// closures), so it only runs on spawn and restore.
internal static class EnemyFactory
{
    // Every persisted enemy row has always been written with these, so rows stay byte-equal.
    public const int DefaultAgility = EnemyCombatant.DefaultAgility;
    public const int DefaultEnergy = EnemyCombatant.DefaultEnergy;

    public static EnemyActor Spawn(
        EnemyTemplate template,
        Vector3 position,
        int actorId,
        SceneEventHub events,
        CombatFormulaService formulas)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(formulas);

        StatBlock stats = Clamped(template.Strength, DefaultAgility, template.Vitality, DefaultEnergy, template.MoveSpeed, template.AttackSpeed);
        EnemyCombatDerivedStats derived = formulas.EvaluateEnemyDerivedStats(
            (int)stats[StatId.Strength],
            stats[StatId.AttackSpeed],
            (int)stats[StatId.Vitality],
            template.Level,
            template.AttackIntervalSeconds);

        EnemyCombatant combatant = new(
            () => position,
            events,
            actorId: actorId,
            maxHealth: derived.MaxHealth,
            attackDamage: derived.AttackDamage,
            attackIntervalSeconds: derived.AttackIntervalSeconds,
            anchorHeightOffset: WolfDefinition.CombatAnchorHeight,
            baseStats: stats);
        return Build(combatant, template, position, template.MoveSpeed, template.DisplayName, template.Key, template.Level, position);
    }

    public static EnemyActor Restore(
        in PersistedEnemyInstance row,
        EnemyTemplate template,
        int actorId,
        SceneEventHub events,
        CombatFormulaService formulas,
        Vector3? home = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(formulas);

        // A row is hostile on load: SetBase clamps what the sheet keeps, and the formulas see the same values.
        PersistedCreatureStats stored = row.Stats;
        StatBlock stats = Clamped(stored.Strength, stored.Agility, stored.Vitality, stored.Energy, stored.MovementSpeed, stored.AttackSpeed);
        // The level is hostile too: a row outside 1..the content maximum is clamped here. Restore does not
        // rewrite the row; the first checkpoint with a diff writes the clamped value. Kill XP and the wire
        // value agree on one number.
        int level = Math.Clamp(stored.Level, 1, ContentCatalog.MaxEnemyLevel);
        EnemyCombatDerivedStats derived = formulas.EvaluateEnemyDerivedStats(
            (int)stats[StatId.Strength],
            stats[StatId.AttackSpeed],
            (int)stats[StatId.Vitality],
            level,
            intervalOverride: null);

        Vector3 position = row.Position;
        EnemyCombatant combatant = new(
            () => position,
            events,
            actorId: actorId,
            maxHealth: stored.MaxHealth,
            attackDamage: derived.AttackDamage,
            attackIntervalSeconds: derived.AttackIntervalSeconds,
            anchorHeightOffset: WolfDefinition.CombatAnchorHeight,
            baseStats: stats);
        combatant.Health.SetAuthoritativeState(stored.CurrentHealth, stored.MaxHealth);

        EnemyActor actor = Build(combatant, template, position, Math.Max(0f, stored.MovementSpeed), row.DisplayName, row.EnemyKind, level, home ?? position);
        actor.SetFacing(row.Facing);
        return actor;
    }

    private static EnemyActor Build(
        EnemyCombatant combatant,
        EnemyTemplate template,
        Vector3 position,
        float moveSpeed,
        string displayName,
        string enemyKind,
        int level,
        Vector3 home)
    {
        return new EnemyActor(
            combatant,
            position,
            aggroRange: template.AggroRange,
            attackRange: template.AttackRange,
            moveSpeed: moveSpeed,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight,
            displayName: displayName,
            enemyKind: enemyKind,
            level: level,
            visualKey: string.Equals(template.VisualKey, ContentCatalog.DefaultEnemyVisualKey, StringComparison.Ordinal)
                ? null
                : template.VisualKey,
            homePosition: home);
    }

    private static StatBlock Clamped(int strength, int agility, int vitality, int energy, float movementSpeed, float attackSpeed)
    {
        StatBlock block = default;
        block[StatId.Strength] = StatRules.ClampBase(StatId.Strength, strength);
        block[StatId.Agility] = StatRules.ClampBase(StatId.Agility, agility);
        block[StatId.Vitality] = StatRules.ClampBase(StatId.Vitality, vitality);
        block[StatId.Energy] = StatRules.ClampBase(StatId.Energy, energy);
        block[StatId.MovementSpeed] = StatRules.ClampBase(StatId.MovementSpeed, movementSpeed);
        block[StatId.AttackSpeed] = StatRules.ClampBase(StatId.AttackSpeed, attackSpeed);
        return block;
    }
}
