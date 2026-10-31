# Wave G: Characters

Purpose: record wave G of `docs/ledger/plans/2026-10-31-characters-items-enemies-roadmap.md`. Plan: `docs/ledger/plans/2026-10-31-wave-g-plan.md`.

Status: integrated on `integrate/engine-iteration`. Not pushed. Not merged into `master`. Tester and reviewer have not run yet, so this log has no Release measurements.

## Process

1. **C0 (`1030648`).** Contracts, migration 0007, protocol v9.
2. **Merges, no conflicts.** G1 (`c6a9291`), G3 (`22d1c86`), G2 (`19456cf`).
3. **Glue.** See below.

## Glue

- `Wave12DeltaActorLoopbackTests` passes a token. This fixed the 5 tests G2 expected to fail.
- The U2 acceptance test is real: two worlds in one SQLite database, hosted in turn.
- `CharacterJoinSqliteTests` runs the join rules on the real `SqliteAccountRepository` and write queue. `AccountHostHarness.OverSqlite` builds that host.
- `IntegratedServerIdentityTests` covers local play with the real token file across a restart on a new port.
- The perf harness gives each client a stable `DREAM_IDENTITY_FILE` beside the executable.
- `docs/configuration.md` and `docs/architecture.md` are updated.

## Production bug found

The U2 test showed that health leaked between worlds. A character with no world-state row in a world took its HP from the global profile, which is the HP it had in the last world it left. It now arrives at full health (`WorldSessionRuntime.ApplyPersistedPlayerState`).

## Checks on real processes

Run on copies of the 4 local legacy databases (`legacy-e`), Debug build.

- Start migrates v6 to v7 and leaves a `v0006-to-v0007` backup on all 4.
- Claim, rejoin and a case variant pass. Another token is refused with a readable reason, as are null and malformed tokens.
- `release`, then the other token claims. `rename` exits 7 while a server runs, 0 offline, 2 on a conflict and 1 on an invalid name. A renamed key stays reserved.
- `--db backup` and `--worlds copy` work on a claimed database.
- Same-token takeover closes the first client.
- Real desktop clients through `launch-and-summarize.ps1`, two runs in a row, joined. Integrated server, two launches on new ports, joined under one `local` token.

## Notes

- `local` and `127.0.0.1:5057` are different tokens, so one player using both on one database is two accounts. See `docs/configuration.md`.
- Account ids skip: the upsert uses up an AUTOINCREMENT value on every join.
- `DREAM_CLIENT_SESSION_ID` has no effect on the desktop client; `Game` keeps only host, port and name. This was so before wave G.

## Fix loop 2

- Log lines are sanitized in `NetworkDiagnosticsLog.WriteEnabled` (`Sanitize`): control characters and bidi formatting characters are written as `\uXXXX`, so a join name with a newline cannot start a second line in `server.rx`, `server.join-rejected`, `authority.join.*` or any other line.
- New accounts are limited per source address (`NewAccountLimiter`): a burst of 5, then 3 a minute. Loopback is exempt and existing accounts are never limited. See `docs/configuration.md`.
- `launch-and-summarize.ps1` saves `config\desktop-client.txt` before the run and restores it after the clients stop, and its summary has a `JoinRejectedLines` column. The shared `local`/loopback token does not fix the stale name by itself: the harness clients use their own identity files, so a leftover `player_name=codex-client-N` is a character another account owns.
- `ServerConnectionTerrainTransferTests.FaultMidTerrain_ReleasesTheCapture_AndSendsNoMoreTerrain` waits for the release. `Fault` sets `IsFaulted` in the write loop's catch and the capture goes back to the pool in its `finally`, a moment later. Release is guaranteed, only asynchronous; there is no production race.

### Measurement note

`JoinTick_WithAWriteBacklog`'s mean is no longer comparable: the join now lands a tick later. Judge the gate on the join-window total: baseline 12.7 to 12.8 ms, tip 12.7 to 13.0 ms. The heaviest tick is 33.9 to 34.1 ms against 31.6 ms.

### Known limits (accepted)

- The stat sliders (U4) are as documented.
- Hopping between worlds works as a revive. Wave I's respawn work revisits it.
- Homoglyph names (look-alike characters from other scripts) are allowed by policy. Names reserve their exact-case key, and display names are unique only ignoring ASCII case.
- A token whose characters were all released counts as a new account for the per-source limit, since the limit reads the character count from the join's prefetch.
