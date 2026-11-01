-- Migration 0009: enemy formulas and armor (wave I). Formulas seeded once; a row already present
-- (edited by hand) is kept. No table is rebuilt and no enemy row changes: every stored wolf's
-- strength, vitality and attack speed are already its real values.
INSERT INTO combat_formulas (formula_key, expression_text, description, enabled)
VALUES
    ('enemy.attack_damage', 'strength + 2', 'Enemy attack damage.', 1),
    ('enemy.max_health', '50 + vitality * 5', 'Enemy maximum health.', 1),
    ('enemy.attack_interval_seconds', '1.0 / attack_speed', 'Seconds between enemy attacks.', 1),
    ('player.damage_taken_factor', '100 / (100 + armor)', 'Share of incoming damage a player takes.', 1)
ON CONFLICT(formula_key) DO NOTHING;
