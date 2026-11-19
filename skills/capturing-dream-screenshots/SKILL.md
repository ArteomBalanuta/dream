---
name: capturing-dream-screenshots
description: Captures in-engine PNG screenshots of the Dream desktop client with DREAM_HUD_CAPTURE and DREAM_HUD_CAPTURE_SCRIPT, one serial auto-connecting run at a time, for visual verification of assets, lighting, HUD and gameplay. Use when you need to see a change in the real game, compare noon and night or scatter on and off, measure a frame with check_look.py, or diagnose a capture that hangs or writes no PNG.
---

# Capturing Dream Screenshots

A capture run is a normal `dream.exe` that connects to its own integrated server, plays a scripted list of steps,
writes `NN-<step>.png` of the real back buffer, and closes itself. The full step vocabulary is in
`docs/configuration.md`, section "HUD (classic and ImGui)"; the traps are in "Capture traps" just below it.

## Rules

- Every launch is a capture run: `DREAM_AUTO_CONNECT=1`, `DREAM_HUD_CAPTURE=<dir>`, `DREAM_HUD_CAPTURE_SCRIPT=...`,
  `DREAM_AUDIO=0`. It exits by itself, and the window opens without focus. Never launch a bare `dream.exe` that sits on
  the connect screen or main menu.
- Strictly one `dream.exe` at a time. The first locks the world; a second hangs to its timeout. Wait until none runs.
  Never kill a `dream.exe` you did not start; kill only your own, and only after its timeout.
- Never set `DREAM_SERVER_HOST` (it switches to a remote server) and never edit `dream/config/desktop-client.txt`.
- Share one `DREAM_IDENTITY_FILE` per bin. With a local server the character name always comes from
  `desktop-client.txt`, so a fresh identity file is rejected with "That character belongs to someone else".
- No colons in any filename or path you set (the drive letter excepted), the identity file included.
- Use a bin built inside the checkout under test, with its own throwaway database: the world lives at
  `config/player-profiles.db` under the bin folder, so a fresh bin is a fresh world. Never capture against another
  worktree's binaries.

## Workflow

1. Build Release from PowerShell into an absolute path inside this checkout (a POSIX shell mangles the backslashes):

   ```powershell
   dotnet build dream\dream.csproj -c Release "/p:BaseOutputPath=$PWD\artifacts\capture-bin\"
   ```

   The exe lands in `artifacts\capture-bin\Release\net10.0\`. Rebuild after every code or asset change: assets are
   copied into the bin at build time. If the copy fails because the exe is locked, wait for your run to end.
2. Run one capture with the helper (it waits for no `dream.exe`, launches, waits, and kills only its own process on
   timeout):

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\skills\capturing-dream-screenshots\scripts\capture.ps1 `
     -Name ground-noon -TimeOfDay noon -Script 'play,keys:g:3,wait:60,snap,walkto:36:40:1500,waitidle:600,snap'
   ```

   Options: `-Env KEY=VALUE[,KEY=VALUE]` for extra variables, `-Bin <dir>` (default
   `artifacts\capture-bin\Release\net10.0`), `-OutRoot` (default `artifacts\captures`), `-IdentityFile` (default
   `<Bin>\capture-identity.json`), `-TimeoutSeconds` (300), `-WaitForIdleSeconds` (900). Exit 0 = PNGs written,
   1 = timeout or no PNG, 2 = another `dream.exe` never finished. Output: `<OutRoot>\<Name>\NN-<step>.png`,
   `out.log`, `err.log`.
3. Run the next capture only after the previous one returned. For A/B pairs keep everything equal but one variable:
   - `-TimeOfDay noon` and `-TimeOfDay night` (also `dawn`, `dusk`, `midnight`, `HH:MM`; server-side, it moves the
     world clock forward and is saved).
   - `-Env DREAM_GROUND_SCATTER=0` (ground assets off) or `-Env DREAM_WORLD_PROPS=0` (props off).
   - `fight:` steps need `-Env DREAM_MAX_FPS=60`. Held poses need `keysnap:` (`keys:` writes no PNG).
4. Look at the PNGs (Read them). Measure when a number matters:
   `python tools/ui-assets/tests/check_look.py <png> --actor-from-log <out.log>` (A1 value tails, A3 actor contrast).

## Looking at worn and held equipment

Equipment is the one thing a normal capture shows badly: the shipped window puts the whole figure at
**28 x 101 px**, and a helm is then about 14 x 12. Three things together make it inspectable.

1. **Turn on the gallery.** `-Env "DREAM_LOOPBACK_REMOTE_PLAYERS=1"` puts the item gallery in the
   local player's hands AND on two orbiting remote figures: every three seconds each one takes the
   next item in every slot, so the whole catalog cycles with no items, no vendor and no starter kit.
2. **Make the window tall.** Put `size:1600:1800` early in the script. The isometric camera is a
   perspective camera with a fixed vertical FOV, so the figure's pixel height scales with the window:
   1600x1800 gives about **90 x 180 px**, which is where sculpted form and surface detail can be
   judged. Judge the SHIPPED read at the default size as well - detail that only appears at 1600x1800
   is detail the player never sees.
3. **Crop the same window every time.** The camera follows the player, so at 1600x1800 the figure
   lands on the same pixels in every frame of every run: `x 712..900, y 975..1185`. A fixed crop makes
   two runs comparable, which the logged `actorbox` does not - that box is the union of the figure
   and every held and worn box, so a raised blade pulls its centre far above the feet.

A sweep that shows a good spread of the catalog, and the same at night, where torchlight on metal is
what the material response is for:

```powershell
powershell -ExecutionPolicy Bypass -File .\skills\capturing-dream-screenshots\scripts\capture.ps1 `
  -Name equip-noon -TimeOfDay noon -Env "DREAM_LOOPBACK_REMOTE_PLAYERS=1" `
  -Script 'play,keys:g:3,size:1600:1800,wait:60,snap,wait:190,snap,wait:190,snap,wait:190,snap,wait:190,snap,wait:190,snap'
```

The gallery step is chosen from the client clock, so two runs do NOT land on the same gear. Never
compare one frame to one frame; take six or more and compare the SPREAD, or measure the mean over all
frames of a run. For animation and attachment, add `-Env "...,DREAM_MAX_FPS=60"` and
`keysnap:w:25,aim:900:700,holdleft:14,snap` - that walks, then swings, and is where a piece that has
come off its bone shows up.

**What the engine will tell you that a test cannot**, learned the hard way on the sculpted-equipment
wave: whether a procedural surface pattern is above the pixel rate (below it, MSAA averages it to a
flat tone and amplitude will not save it); whether a curved shell is showing its hollow side to the
camera; and whether a piece reads as attached or as a prop floating beside the figure. All three were
invisible to every gate and obvious in the first frame.

## When a run writes no PNG

All traps look the same: no PNG, a hang to the timeout, last stdout line
`[startup] GL debug output: high and medium severity`. Read the logs before relaunching:

1. Another `dream.exe` was running: the helper waits for it; never start two.
2. `err.log`: `[startup] local server not started: World '1' is already in use` or `World '7' does not exist`.
3. `out.log`: `[session] ended: Join rejected: That character belongs to someone else` -> you used a new identity
   file against a bin whose database already has the character; reuse that bin's identity file.
4. A colon in a path you set.
5. The bin is from another checkout or is stale: rebuild in this checkout.

Do not loop relaunches on a failure; each one costs a timeout. Fix the cause shown in the logs first.
