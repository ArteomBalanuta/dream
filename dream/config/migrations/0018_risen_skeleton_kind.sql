-- Migration 0018: the Risen Skeleton's per-kind tuning row.
--
-- A fifth kind joins enemy_kinds (see 0017 for why the table is global and what its CHECKs require).
-- The row equals what enemies.json authors for RisenSkeleton (a test pins that). ON CONFLICT DO NOTHING
-- keeps a hand-edited row from ever being overwritten. Nothing is rewritten in enemy_instances: no
-- stored enemy can be a RisenSkeleton before this migration.

INSERT INTO enemy_kinds (enemy_kind, chase_speed, patrol_speed, attack_interval_seconds, attack_range, aggro_range)
VALUES
    ('RisenSkeleton', 3.6, 1.25, 1.45, 1.5, 13)
ON CONFLICT(enemy_kind) DO NOTHING;
