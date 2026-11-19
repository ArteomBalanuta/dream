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
   `<OutRoot>\<Name>\data`, wiped first unless `-KeepData`; passed as `DREAM_DATA_DIR`; relative paths resolve against the shell's location; the wipe happens only for a folder under `-OutRoot` or one holding the `.dream-capture-data` marker this script wrote when it created it, and never for a drive root, the repo, the bin, your home folder or any ancestor of them, otherwise it refuses and asks for `-KeepData` or another folder), `-Seed <n>` (default 1;
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

## Before you capture equipment at all: the model sheet

A capture is the wrong first tool for a geometry question. The gallery step is chosen from the
client clock, so you cannot ask a run for a named piece, and a Release build plus two runs is
minutes. `tools/ui-assets/tests/modelsheet.py` composes every model in both equipment catalogues out
of the unit meshes the engine actually builds and draws all 50, by name, at the shipped camera
angle, in about a second:

```powershell
$dump = "$env:TEMP\shapes"
$env:DREAM_SHAPE_DUMP = $dump
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~EveryShapeDumpsToObj" -v q --nologo
python tools\ui-assets\tests\modelsheet.py $dump armour.png armour
python tools\ui-assets\tests\shapesheet.py $dump shapes.png     # the primitives alone
```

It is NOT the engine: no tint, no equipment pattern, no point lights, no shadow, one constant gain.
Use it for "is this piece the shape I meant, does it read as one object, is any part a floating
slab", and use a capture for everything in the list at the end of the next section. In pass 4 the
sheet found three defects in one picture each - a Lame whose steps were 0.01 of a radius and
rendered as a smooth taper, a wing whose ribs were a third of a pixel deep, and five back items
reading as loose rectangles - and every one of them was green on every gate.

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

1. Another `dream.exe` was running on the same data dir (`-Serial` waits for any `dream.exe`); never share one.
2. `err.log`: `[startup] local server not started: World '1' is already in use` or `World '7' does not exist`.
3. `out.log`: `[session] ended: Join rejected: That character belongs to someone else` -> you used a new identity
   file against a bin whose database already has the character; reuse that bin's identity file.
4. A colon in a path you set.
5. The bin is from another checkout or is stale: rebuild in this checkout.

Do not loop relaunches on a failure; each one costs a timeout. Fix the cause shown in the logs first.
