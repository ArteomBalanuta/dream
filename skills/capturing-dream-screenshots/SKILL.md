---
name: capturing-dream-screenshots
description: Captures in-engine PNG screenshots of the Dream desktop client with DREAM_HUD_CAPTURE and DREAM_HUD_CAPTURE_SCRIPT, auto-connecting runs that may go in parallel (one data dir each), for visual verification of assets, lighting, HUD and gameplay. Use when you need to see a change in the real game, compare noon and night or scatter on and off, measure a frame with check_look.py, or diagnose a capture that hangs or writes no PNG.
---

# Capturing Dream Screenshots

A capture run is a normal `dream.exe` that connects to its own integrated server, plays a scripted list of steps,
writes `NN-<step>.png` of the real back buffer, and closes itself. The full step vocabulary is in
`docs/configuration.md`, section "HUD (classic and ImGui)"; the traps are in "Capture traps" just below it.

## Rules

- Every launch is a capture run: `DREAM_AUTO_CONNECT=1`, `DREAM_HUD_CAPTURE=<dir>`, `DREAM_HUD_CAPTURE_SCRIPT=...`,
  `DREAM_AUDIO=0`. It exits by itself, and the window opens without focus. Never launch a bare `dream.exe` that sits on
  the connect screen or main menu.
- One `dream.exe` per data dir. The helper gives every run its own `DREAM_DATA_DIR` (default
  `<OutRoot>\<Name>\data`, wiped before the run), so runs with distinct `-Name` may run in parallel. Two runs on one
  data dir lock the same world and the second hangs to its timeout. Never kill a `dream.exe` you did not start; kill
  only your own, and only after its timeout.
- Never set `DREAM_SERVER_HOST` (it switches to a remote server) and never edit `dream/config/desktop-client.txt`.
- The default identity lives in the data dir, so it is fresh with the world. Pass `-IdentityFile` only to share one
  identity across runs that also share a kept data dir (`-KeepData`); a fresh identity against an old database is
  rejected with "That character belongs to someone else".
- No colons in any filename or path you set (the drive letter excepted), the data dir and identity file included.
- Use a bin built inside the checkout under test. The world no longer lives under the bin folder (it is in the data
  dir), but never capture against another worktree's binaries.
- The simulation seed is fixed (`-Seed`, default 1) so two runs of one script match.

## Workflow

1. Build Release from PowerShell into an absolute path inside this checkout (a POSIX shell mangles the backslashes):

   ```powershell
   dotnet build dream\dream.csproj -c Release "/p:BaseOutputPath=$PWD\artifacts\capture-bin\"
   ```

   The exe lands in `artifacts\capture-bin\Release\net10.0\`. Rebuild after every code or asset change: assets are
   copied into the bin at build time. If the copy fails because the exe is locked, wait for your run to end.
2. Run a capture with the helper (it launches, waits, and kills only its own process on timeout):

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\skills\capturing-dream-screenshots\scripts\capture.ps1 `
     -Name ground-noon -TimeOfDay noon -Script 'play,keys:g:3,wait:60,snap,walkto:36:40:1500,waitidle:600,snap'
   ```

   Options: `-Env KEY=VALUE[,KEY=VALUE]` for extra variables, `-Bin <dir>` (default
   `artifacts\capture-bin\Release\net10.0`), `-OutRoot` (default `artifacts\captures`), `-DataDir <dir>` (default
   `<OutRoot>\<Name>\data`, wiped first unless `-KeepData`; passed as `DREAM_DATA_DIR`), `-Seed <n>` (default 1;
   `DREAM_SIMULATION_SEED`), `-FromSource` (`DREAM_ASSET_ROOT=<repo>\dream`: edited JSON, atlases and shaders apply
   on the next launch with no rebuild), `-Serial` (wait for any running `dream.exe` first, the old behaviour),
   `-IdentityFile` (only when given), `-AllowDevErrors`, `-TimeoutSeconds` (300), `-WaitForIdleSeconds` (900, with
   `-Serial`). Exit codes: 0 = PNGs written, 1 = timeout or no PNG, 2 = `-Serial` wait aborted, 3 = a `dev:` step
   replied `[dev] error:` or got no reply (unless `-AllowDevErrors`). Output: `<OutRoot>\<Name>\NN-<step>.png`,
   `out.log`, `err.log`; every `[hud-capture] dev` line is echoed in the summary.
3. Parallel captures: launch several with distinct `-Name` (one PowerShell job or process each); each gets its own
   world and data dir. Only a capture on a kept, shared data dir must wait for the previous one.
   Staging and time of day inside a script:
   - `dev:clear;spawn:wolf:2:3` sends `/dev clear; spawn wolf 2 3` (colons become spaces, `;` separates
     subcommands, at most 8, no commas) and waits for one reply per subcommand. Writes no PNG. Needs dev commands on
     (integrated local server) and a loopback connection.
     Example: `play,keys:g:3,dev:clear;spawn:wolf:1:3,wait:20,aimenemy,status,snap`.
   - `tod:<hours>|off` sets the client-only visual time of day (decimal hours in [0,24), e.g. `tod:21.5`); `off`
     returns to the world clock. Writes no PNG; no server change.
4. For A/B pairs keep everything equal but one variable:
   - `-TimeOfDay noon` and `-TimeOfDay night` (also `dawn`, `dusk`, `midnight`, `HH:MM`; server-side, it moves the
     world clock forward and is saved).
   - `-Env DREAM_GROUND_SCATTER=0` (ground assets off) or `-Env DREAM_WORLD_PROPS=0` (props off).
   - `fight:` steps need `-Env DREAM_MAX_FPS=60`. Held poses need `keysnap:` (`keys:` writes no PNG).
5. Look at the PNGs (Read them). Measure when a number matters:
   `python tools/ui-assets/tests/check_look.py <png> --actor-from-log <out.log>` (A1 value tails, A3 actor contrast).

## When a run writes no PNG

All traps look the same: no PNG, a hang to the timeout, last stdout line
`[startup] GL debug output: high and medium severity`. Read the logs before relaunching:

1. Another `dream.exe` was running on the same data dir (`-Serial` waits for any `dream.exe`); never share one.
2. `err.log`: `[startup] local server not started: World '1' is already in use` or `World '7' does not exist`.
3. `out.log`: `[session] ended: Join rejected: That character belongs to someone else` -> you used a new identity
   file against a bin whose database already has the character; reuse that bin's identity file.
4. A colon in a path you set.
5. The bin is from another checkout or is stale: rebuild in this checkout.

Do not loop relaunches on a failure; each one costs a timeout. Fix the cause shown in the logs first.
