# Dream Persistence State Skill

Purpose: Record the repo-local skill added for reading and editing Dream's authoritative SQLite persistence state.
Status: Completed
Scope: Skill authoring, live DB inspection, and persistence update workflow support.

## Summary

Added `skills/updating-dream-persistence-state/` to support persistence-aware agent work against the live Dream server database.

The skill:

- points agents at the authoritative DDL in `dream/config/player-profiles.init.sql`
- points agents at the schema guide in `docs/server-persistence-ddl.md`
- provides `scripts/dream_persistence_sqlite.py` to locate the newest runtime `player-profiles.db`, inspect tables and player rows, create backups, and perform targeted updates for:
  - player world position
  - player base stats and experience
  - enemy position and health state
  - arbitrary SQL fallbacks when a targeted helper is not enough
- provides `references/schema-and-operations.md` with table-oriented guidance for players, enemies, cubes, fracture pieces, items, and inventory rows

## Verification

- Confirmed the live schema is bootstrapped from `dream/config/player-profiles.init.sql` by `dream/Server/ServerBootstrap.cs`.
- Ran the helper in read-only mode to resolve the current database path and list live tables.
- Ran a repo-local manual skill structure check for `SKILL.md`, `agents/openai.yaml`, `scripts/`, and `references/`.

## Notes

The helper resolves the newest runtime database under `dream/bin/**/config/player-profiles.db`, which matches the current `AssetPaths.Config("player-profiles.db")` server bootstrap flow.
