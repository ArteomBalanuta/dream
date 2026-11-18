-- Migration 0017: per-kind enemy tuning, and the wolf family's slower chase and longer bite interval.
--
-- enemy_kinds is a GLOBAL table (no world id): one row per enemy kind with the five numbers a designer
-- tunes by hand - chase speed, patrol speed, seconds between attacks, attack range and aggro range.
-- The server reads it at boot and applies it to new spawns and to restored enemies alike, so a row
-- edited in SQL and a restart is all it takes. A kind with no row, or a row that fails the content
-- rules, keeps the values enemies.json authors. The four seeded rows equal enemies.json (a test pins
-- that), and the seed never overwrites a row that was edited by hand.
--
-- The UPDATE below rewrites enemy_instances.movement_speed and attack_speed for every stored enemy of
-- a kind that has a row here. It is gated on the KIND, never on the stored value: these columns were
-- never per-instance numbers - every spawn wrote its template's values - so a stored 5.0 is the old
-- wolf and not something a player or an admin chose. (A gate on "value X" would also be wrong for a
-- value that was once a clamp ceiling.) Rows of kinds not listed here are left exactly as stored.
-- The columns stay informational afterwards: restore takes both numbers from the kind.

CREATE TABLE IF NOT EXISTS enemy_kinds (
    enemy_kind TEXT NOT NULL PRIMARY KEY,
    chase_speed REAL NOT NULL,
    patrol_speed REAL NOT NULL,
    attack_interval_seconds REAL NOT NULL,
    attack_range REAL NOT NULL,
    aggro_range REAL NOT NULL,
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    CHECK (length(trim(enemy_kind)) > 0),
    CHECK (chase_speed > 0),
    CHECK (patrol_speed > 0 AND patrol_speed <= chase_speed),
    CHECK (attack_interval_seconds >= 0.05),
    CHECK (attack_range > 0),
    CHECK (aggro_range > 0)
);

INSERT INTO enemy_kinds (enemy_kind, chase_speed, patrol_speed, attack_interval_seconds, attack_range, aggro_range)
VALUES
    ('Wolf', 3.0, 1.0, 1.8, 1.35, 10),
    ('DireWolf', 3.3, 1.1, 1.6, 1.35, 12),
    ('AshenProwler', 3.5, 1.2, 1.5, 1.35, 12),
    ('GraveHowler', 3.8, 1.3, 1.4, 1.35, 14)
ON CONFLICT(enemy_kind) DO NOTHING;

UPDATE enemy_instances
SET movement_speed = (SELECT chase_speed FROM enemy_kinds WHERE enemy_kinds.enemy_kind = enemy_instances.enemy_kind),
    attack_speed = 1.0 / (SELECT attack_interval_seconds FROM enemy_kinds WHERE enemy_kinds.enemy_kind = enemy_instances.enemy_kind)
WHERE enemy_kind IN (SELECT enemy_kind FROM enemy_kinds);
