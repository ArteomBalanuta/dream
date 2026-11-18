using OpenTK.Mathematics;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Enemies;

// The one place an enemy is built, for a new spawn and for a restore from a row, so the two cannot
// drift. Strength, Vitality, Attack Speed and Level are instance stats (they come from the template at
// spawn and from the row at restore); damage is derived from them through the enemy.* formulas, and
// maximum health is derived at spawn and read from the row at restore. The chase speed, the attack
// interval, aggro and attack range, the body, the visual and loot come from the template (the
// authored kind, or its enemy_kinds tuning). Allocates (the actor, its closures), so it only runs on
// spawn and restore.
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
            maxHealth: template.MaxHealth ?? derived.MaxHealth,
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
        StatBlock stats = Clamped(stored.Strength, stored.Agility, stored.Vitality, stored.Energy, template.MoveSpeed, stored.AttackSpeed);
        // The level is hostile too: a row outside 1..the content maximum is clamped here. Restore does not
        // rewrite the row; the first checkpoint with a diff writes the clamped value. Kill XP and the wire
        // value agree on one number.
        int level = Math.Clamp(stored.Level, 1, ContentCatalog.MaxEnemyLevel);
        // The health pool is hostile too, and it is the one stat no StatId clamp covers: a row outside
        // 1..the content ceiling (the same bound the enemy sheet is validated against) is clamped here,
        // so a tampered row cannot restore an enemy nobody can kill. Current health follows the clamped
        // maximum (SetAuthoritativeState clamps it into [0, max]).
        int maxHealth = Math.Clamp(stored.MaxHealth, 1, ContentCatalog.MaxEnemyMaxHealth);
        EnemyCombatDerivedStats derived = formulas.EvaluateEnemyDerivedStats(
            (int)stats[StatId.Strength],
            stats[StatId.AttackSpeed],
            (int)stats[StatId.Vitality],
            level,
            // The attack interval is a KIND stat, like the chase speed below: a restored enemy bites at
            // the rate its kind is authored (or tuned in enemy_kinds) with, not at the one its row's
            // attack_speed would derive.
            intervalOverride: template.AttackIntervalSeconds);

        Vector3 position = row.Position;
        EnemyCombatant combatant = new(
            () => position,
            events,
            actorId: actorId,
            maxHealth: maxHealth,
            attackDamage: derived.AttackDamage,
            attackIntervalSeconds: derived.AttackIntervalSeconds,
            anchorHeightOffset: WolfDefinition.CombatAnchorHeight,
            baseStats: stats);
        // BH-DEATH-2: current health is hostile too, and the schema's CHECK is only (current_hp >= 0), so a
        // row of 0 is storable. A kill deletes its row and a checkpoint skips an inactive enemy, so a stored
        // 0 can only be a tampered, cloned or legacy row - and restoring it made a permanently dead enemy:
        // PruneInactive drops it from the registry with no credit and NO row delete, so it came back and
        // burned one more enemy actor id on every restart, forever. Clamp to at least 1, like the maximum
        // two lines above: the enemy restores killable, the next checkpoint writes the clamped value back,
        // and killing it deletes the row.
        combatant.Health.SetAuthoritativeState(Math.Clamp(stored.CurrentHealth, 1, maxHealth), maxHealth);

        // The chase speed is a KIND stat: it comes from the template (authored, or tuned in enemy_kinds),
        // never from the row's movement_speed, which is informational. A row that is NaN, zero or huge
        // therefore cannot freeze the enemy or close the whole gap in a tick. The template value goes
        // through the same clamp as a spawn's, so the replicated sheet and the actor agree.
        EnemyActor actor = Build(combatant, template, position, stats[StatId.MovementSpeed], row.DisplayName, row.EnemyKind, level, home ?? position);
        // The facing is hostile too, and facing_x/y/z carry no CHECK either. FaceDirection only rejects a
        // near-zero direction: a non-finite one normalizes to a non-finite Forward, which poisons every
        // later facing comparison and the wire value. Such a row keeps the actor's default forward instead.
        if (float.IsFinite(row.Facing.X) && float.IsFinite(row.Facing.Y) && float.IsFinite(row.Facing.Z))
        {
            actor.SetFacing(row.Facing);
        }

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
            // The chase speed and the patrol walk both come from the kind; the patrol walk is capped at
            // the chase speed it was given.
            patrolSpeed: Math.Min(template.PatrolSpeed, moveSpeed),
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

    // Enemies are clamped against the enemy ceiling (StatRules.ClampEnemyBase), not the player one: the
    // player ceiling is 10000 and `enemy.attack_damage = strength + 2`, so a tampered row would otherwise
    // one-shot anyone. See StatRules.MaxEnemyBase.
    private static StatBlock Clamped(int strength, int agility, int vitality, int energy, float movementSpeed, float attackSpeed)
    {
        StatBlock block = default;
        block[StatId.Strength] = StatRules.ClampEnemyBase(StatId.Strength, strength);
        block[StatId.Agility] = StatRules.ClampEnemyBase(StatId.Agility, agility);
        block[StatId.Vitality] = StatRules.ClampEnemyBase(StatId.Vitality, vitality);
        block[StatId.Energy] = StatRules.ClampEnemyBase(StatId.Energy, energy);
        block[StatId.MovementSpeed] = StatRules.ClampEnemyBase(StatId.MovementSpeed, movementSpeed);
        block[StatId.AttackSpeed] = StatRules.ClampEnemyBase(StatId.AttackSpeed, attackSpeed);
        return block;
    }
}
