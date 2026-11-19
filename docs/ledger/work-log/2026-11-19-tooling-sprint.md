# Work log: tooling sprint

- Purpose: record the run directories, `/dev` staging, capture steps, render verification, content validator and
  workspace tools.
- Status: done, on branch `tool/int` from `33c5f5e4`; golden baselines are recorded from real captures in a
  follow-up step.
- Plan: `docs/ledger/plans/2026-11-19-tooling-sprint.md`.

## What landed

- **Run directories.** `Dream.Assets.DataPaths` (`DREAM_DATA_DIR`: database, `desktop-client.txt`, default
  identity) and `AssetPaths.RuntimeRoot` (`DREAM_ASSET_ROOT`). `ServerBootstrap.DefaultDatabasePath`, `Game`'s
  desktop config, `ClientIdentityStore.ForCurrentUser`, `ContentCatalog.ShippedDirectory` and
  `AudioClipBank.DefaultDirectory` follow them. `StartupConnectionScene.IsAutoConnectEnabled` treats a capture as
  auto-connect. Tests: `RunDirectoryPathsTests`, `ClientIdentityDataDirTests`, `CaptureAutoConnectTests`.
- **Dev commands.** `DevCommands.TryParse`, `DedicatedServerHost.HandleDevCommand` (routed before the admin path)
  and the `WorldSessionRuntime` `Dev*` operations. The gate is read under the tick lock for every subcommand. Spawned
  enemies carry no camp, so they are not restored next start. `ChatInput` sends `/dev` as a `Command` line.
- **Capture steps.** `HudCaptureDevStep`, `HudCaptureVerb.Dev` and `HudCaptureVerb.TimeOfDay`; `PlayScene` queues the
  `/dev` line, counts `[dev] ` replies in the chat intake and logs each one, and drives `Render.TimeOfDayOverride`.
  `capture.ps1` gains `-DataDir`, `-KeepData`, `-Seed`, `-FromSource`, `-Serial`, `-AllowDevErrors` and exit code 3.
- **Render verification.** `ShaderCompileTests` compile and link 15 programs in a hidden 64x64 GL context on a
  dedicated thread; coverage is derived from `dream/shaders`; negatives: an injected syntax error and an orphan
  `#version` file. `tools/golden` (`golden.py`, `run.ps1`, three scenes, `test_golden.py`).
- **Content validator.** `ContentValidateCli` behind `dream --validate-content`; about 0.26 s wall clock on shipped
  content.
- **Workspace tools.** `tools/dev/worktrees.ps1`, `tools/dev/test.ps1`, `tools/dev/gallery.py`; README, overview,
  build-and-run, configuration and AGENTS docs updated; `docs/architecture.md` protocol number corrected to 22.

## Cut, and why

- **In-process golden rendering.** It needs a server-less render path, which is an engine change; golden scenes
  run the real exe through `capture.ps1`, which tests what ships.
- **Live hot reload.** A file watcher would have to rebuild GL resources mid-frame against the shader lifetime
  rules; `DREAM_ASSET_ROOT` gives next-launch reload with no rebuild at a fraction of the risk.
- **Bot command line.** Capture scripts plus `dev:` steps already stage and drive a session end to end.
- **`/dev time`.** Changing the world clock is server-wide and persisted; the client-only `tod:` covers what
  captures need.
- **`/dev god`** (and `/dev level`, kills with loot or XP). They change combat or progression rules, which is
  exactly where a staging tool could leak an exploit; `heal`, `hp` and `mana` cover the staging need.

## Findings worth keeping

- Shipped content has two real gaps the validator now reports: `skill.bolt` and `skill.mend` have no icon sprite.
- `dev give` of a non-rolled item has no free-grant helper in the inventory service; it buys at price 1 and refunds
  the coin, with the inventory action gate lifted for the call. A `TryGrantFixed` there would remove the trick.
- `dev tp` moves the seat like a respawn and relies on the client's correction snap; a `status` straight after it
  may need a few frames to settle.
- The worktree inventory found 256 worktrees holding about 130 GB of build output; the tool reports and cleans
  build output only, and never removes a dirty, locked or unmerged worktree.
