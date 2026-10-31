# Wave F: Stats, Content Catalog, Hardening

Purpose: record wave F of `docs/ledger/plans/2026-10-31-characters-items-enemies-roadmap.md` and its two fix loops. Wave F also closes persistence Phase 5.

Status: integrated on `integrate/engine-iteration` at `bd17386`. Not pushed. Not merged into `master`. Interactive play has not been checked. Schema stays at v6, protocol at v8. Gameplay is unchanged.

## Process

1. **Roadmap (`3f46652`).** The architect's roadmap. Wave F needs no user decision. Decisions U1–U10 stay open and block waves G to I.
2. **Implementation.** Three implementers ran in parallel: F1 stat core, F2 content catalog, F3 hardening. They merged in that order with no conflicts.
3. **Glue (`337a948`).**
   - Content is read once at server start, and bad content exits 8.
   - The integrity check got its kind predicates from the catalog.
   - A second desktop client on a held world now stays usable.
   - All 9 real database copies started with no findings.
4. **Verification on `337a948`.**
   - Reviewer: 2 Must fix (restore can't repair a damaged database; restore accepts an empty or foreign file), 10 Should fix.
   - Tester: all gates passed, and two flaky tests were fixed (`ee0e6b7`). It confirmed the client content crash, the restore defect and the lease error handling by running real processes.
5. **Fix loop 1 (`76a9b73`, merged as `43e854f`).** Covered the review findings.
6. **Re-review of loop 1.** 1 Must fix: Use Local Server during a slow Connect started a second connection. 6 Should fix.
7. **Fix loop 2 (`bd17386`).** Covered the re-review, plus shared-item detection, content text limits and a faster stat test.

## Changes

| Area | Change |
| --- | --- |
| Stats | `Simulation/Stats`: `StatId`, `StatModifier`, `StatBlock`, `StatRules`, `StatSheet`. Resolution is `clamp((base + flat) * (1 + percent))`, in fixed slot order, only when an input changes, with no allocation. `PlayerCombatant` runs on a sheet and matches the old `Combine` bit for bit. Effective stats stay out of checksums. |
| Content | `config/content/items.json` and `enemies.json`, loaded by `ContentCatalog` with strict validation and a logged digest. `ToolCatalog` is a lazy facade with `TryResolve`. A retired item kind is inert and an unknown enemy kind is skipped on restore; both keep their rows. Keys, texts and `visualId` have limits. |
| Host lease | `{db}.world-{id}.lock`, one server per world, exit 7. A crash frees it. Offline `--worlds` commands refuse a held world. |
| Maintenance lock | `{db}.maintenance.lock`, held by restore, by migrations and by `--worlds` commands. When migrations are pending, every world's lease is required, so a refused start can't migrate a database in use. The world record is re-read after the lease. Dedicated starts wait 20 s; integrated starts wait 250 ms so the window doesn't freeze. |
| Backup | `--db backup <path>`: safe while running, integrity-checked, never overwrites. |
| Restore | `--db restore <path> --yes`: whole file, every world must be free. The source must contain `worlds` or `player_profiles`. Keeps a `.bak`, with a raw copy as the fallback when the live file is damaged. The WAL moves aside and goes back if the swap fails. |
| Integrity check | Runs at start. Foreign-key or corruption failures exit 6. Out-of-bounds objects, unmatched equipped slots, unknown kinds and shared item references are logged only. |
| Second client | A desktop client whose world is held stays up, shows why, and can connect elsewhere. Use Local Server retries and no longer overwrites a typed address. |
| Client content | A client reads content at start and shows a failure on the connection screen, instead of crashing during play. |
| Exit codes | New: 6 integrity, 7 world held or database busy, 8 invalid content. |

## Measured Impact

Release, `DREAM_LOAD_TEST=1`, alternating against `3f46652`, on `337a948`. **Not repeated after the fix loops.** Those touched start, restore and client paths, not the tick or checkpoint code.

| Gate | Before | Wave F | Result |
| --- | --- | --- | --- |
| Tick goldens | pass | identical | pass |
| Small join | 8,492 B | identical SHA | pass |
| World10k join 0 | 232,677 B | 232,677 B | pass |
| Tick allocation | 0 B | 0 B | pass |
| World10k full checkpoint | 37.0 ms | 38.0 ms | 1.03×, limit 1.05× |
| Integrity check, World10k | | 8.6–11.9 ms with `quick_check` | budget 15 ms |
| Server start, World10k | 87.9 ms | 106.8 ms | no clear difference, ±2× noise |

## Verification

- **Full suite on `bd17386`:** 3,870 passed, 43 skipped (load tests only), 0 failed.
- **`TickPacerTests`:** it failed once more in loop 2 even after the tester's fix, then passed the next full run and 8 of 8 alone on `bd17386`. It stays a timing risk under load.
- **Backup under load:** 3 backups during about 29,000 commits were all consistent. Restoring one over a hard-killed server gave the backup's exact state, and the `.bak` held the full old state.
- **Races:** ten simultaneous `--worlds copy` commands gave clean successes and refusals, with no duplicates.
- **Perf harness on `337a948`:** both clients at 144 FPS, 31 draws, 0 GL errors.

## Known Limits And Follow-ups

- **Two desktop clients can't both host one world.** The second must join by address. Use a dedicated server for two players on one machine.
- **Lease error handling on Linux and macOS is unverified.** It ran on Windows only.
- **The Use Local Server button itself is untested.** The tests cover the coordinator; the scene needs GL.
- **Real-process attacks were not rerun after the fix loops.** The confirmed defects have code-level tests instead.
- **Wave G needs decisions U1, U2 and U10.**
