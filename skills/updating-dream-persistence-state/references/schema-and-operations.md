# Dream Persistence Schema And Operations

Read this only when you need table-level update guidance beyond the main skill.

## Authoritative Inputs

- DDL: [dream/config/player-profiles.init.sql](/G:/workspace/projects/dream/dream/config/player-profiles.init.sql)
- DDL guide: [docs/server-persistence-ddl.md](/G:/workspace/projects/dream/docs/server-persistence-ddl.md)
- Bootstrap path: [dream/Server/ServerBootstrap.cs](/G:/workspace/projects/dream/dream/Server/ServerBootstrap.cs)

The live runtime database is `config/player-profiles.db` under the built executable directory. The helper script locates the newest matching file under `dream/bin/**/config/player-profiles.db`.

## Common Tables

### `player_profiles`

Use for player-owned persistent stats and identity values:

- `player_name`
- `strength`
- `vitality`
- `attack_speed`
- `level`
- `experience`
- `last_server_endpoint`

Typical updates:

- rebalance base stats
- restore or move progression
- fix per-player values that accidentally leaked across clients

### `player_world_state`

Use for a player's persisted world transform and health:

- `player_name`
- `position_x`, `position_y`, `position_z`
- `yaw`, `pitch`
- `current_hp`
- `is_flying_enabled`
- `selected_hotbar_slot`

Typical updates:

- move a player to a safe spawn
- restore HP
- reset camera-facing state

### `enemy_instances`

Use for authoritative enemy state:

- `enemy_id`
- `enemy_name`
- `position_x`, `position_y`, `position_z`
- `current_hp`, `max_hp`
- `level`
- `is_alive`

Typical updates:

- move or revive enemies
- repair bad HP state
- rename or reclassify test enemies

### `placed_cube_instances`

Use for placed/destructible world cubes:

- `cube_id`
- `asset_key`
- `material_key`
- `position_x`, `position_y`, `position_z`
- `size_x`, `size_y`, `size_z`
- `is_destroyed`

Typical updates:

- move or resize cubes
- mark a bad cube as destroyed

### `cube_fracture_pieces`

Use for destruction debris that should survive reconnects:

- `piece_id`
- `source_cube_id`
- `position_x`, `position_y`, `position_z`
- `velocity_x`, `velocity_y`, `velocity_z`
- `size_x`, `size_y`, `size_z`
- `is_active`

Typical updates:

- reposition overlapping debris
- deactivate broken pieces

### `item_instances`

Use for world items/equipment state:

- `item_id`
- `owner_player_name`
- `item_name`
- `item_kind`
- `equip_slot`
- `damage_min`, `damage_max`
- `attack_speed_bonus`
- `position_x`, `position_y`, `position_z`
- `is_equipped`

Typical updates:

- tune a weapon
- move a dropped item
- fix equip state

### `player_inventory_slots`

Use for server-authoritative inventory contents:

- `player_name`
- `slot_index`
- `item_id`
- `stack_count`
- `is_equipped`

Typical updates:

- repair inventory/equip mismatches
- seed test loadouts

## Safety Rules

- Prefer targeted helper commands for common updates before raw SQL.
- Always create a backup before write operations unless the task explicitly forbids it.
- Stop the running game/server before invasive edits if the change touches many rows or identity keys.
- Keep player identity keyed by `player_name` unless a task explicitly changes schema.
- After updates, re-query the touched rows and verify the expected values.
