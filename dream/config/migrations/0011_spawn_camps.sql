-- Migration 0011: spawn camps (M2-B). An enemy row remembers the camp it belongs to by key, so a
-- restored enemy keeps its home and counts toward its camp. NULL is every row written before camps
-- (and an enemy spawned by the world-wide wolf population step): it keeps working, restored at the
-- home its role gives it and never respawned by a camp. No foreign key: camps are content, and a
-- camp that was removed from spawns.json leaves its rows as legacy ones. No table is rebuilt, so the
-- indexes and trg_enemy_instances_advance_object_id stay as they are.
ALTER TABLE enemy_instances ADD COLUMN camp_key TEXT NULL;
