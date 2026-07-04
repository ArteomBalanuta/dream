---
name: updating-dream-persistence-state
description: Reads Dream's authoritative SQLite DDL and live persistence database, then safely inspects or updates player profiles, player world positions, enemies, items, cubes, and other server-owned state. Use when debugging persistence, repairing rows, seeding test state, or moving players/entities without guessing the schema.
---

# Updating Dream Persistence State

Use this skill when work depends on the real Dream persistence schema and the live SQLite database, especially for:

- reading or validating the authoritative DDL before editing state
- moving players or enemies in persisted world state
- fixing per-player stats, progression, or HP
- inspecting inventory, items, cubes, or fracture-piece rows
- seeding or repairing server-owned test data between runs

## Workflow

1. Read the authoritative schema sources first:
   - [dream/config/player-profiles.init.sql](/G:/workspace/projects/dream/dream/config/player-profiles.init.sql)
   - [docs/server-persistence-ddl.md](/G:/workspace/projects/dream/docs/server-persistence-ddl.md)
2. Resolve the live database path with:

```powershell
python skills/updating-dream-persistence-state/scripts/dream_persistence_sqlite.py locate-db
```

3. Inspect before writing:

```powershell
python skills/updating-dream-persistence-state/scripts/dream_persistence_sqlite.py show-player --name "dream-client"
python skills/updating-dream-persistence-state/scripts/dream_persistence_sqlite.py tables
```

4. Prefer targeted write commands for common updates. Add `--backup` unless the task explicitly says not to:

```powershell
python skills/updating-dream-persistence-state/scripts/dream_persistence_sqlite.py update-player-position --name "dream-client" --x 32 --y 2 --z 32 --backup
python skills/updating-dream-persistence-state/scripts/dream_persistence_sqlite.py update-player-stats --name "dream-client" --strength 12 --vitality 9 --attack-speed 1.4 --level 3 --experience 220 --backup
python skills/updating-dream-persistence-state/scripts/dream_persistence_sqlite.py update-enemy --enemy-id "wolf-001" --x 36 --y 2 --z 30 --current-hp 75 --is-alive 1 --backup
```

5. Use raw SQL only when targeted helpers are not enough:

```powershell
python skills/updating-dream-persistence-state/scripts/dream_persistence_sqlite.py run --sql "UPDATE placed_cube_instances SET is_destroyed = 1 WHERE cube_id = \$cubeId" --params "{\"cubeId\":\"cube-123\"}" --backup
```

6. Re-query the touched rows and report the exact values you verified.

## Guardrails

- Treat the server database as authoritative. Do not invent columns or table names.
- Keep identity updates narrow. `player_name` is the join key across profile, world, and inventory tables.
- Prefer changing persisted state while the game/server is stopped if many rows or identity-bearing rows are involved.
- When debugging shared-state bugs, inspect both `player_profiles` and `player_world_state` so stat leaks and position leaks are not conflated.

## Reference

For table-by-table guidance and common update targets, read [references/schema-and-operations.md](/G:/workspace/projects/dream/skills/updating-dream-persistence-state/references/schema-and-operations.md).
