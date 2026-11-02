# Plan: isometric view and click-to-move

**Goal:** make the fixed high-angle camera the only play view, replace WASD with left-click movement using a small client-side A*, and leave the server, protocol and goldens unchanged.

**Evidence level:** this plan comes from reading code and a trial merge. Nothing was built or run. All constants below are starting values, and nobody has played the result by hand.

## Spike verdict
- `git merge-tree 7264b34 4279e89` is clean, because master has not touched any of the spike's 12 files.
- **Do not merge it anyway.** It is env-flagged and bends seams. Rewrite it properly.
- **Port:** the camera vector maths, the projection values, the silhouette block and the cursor-ray maths.
- **Drop:** `IsoCameraSpike`, cutaway, ortho, self-tests, reconciler counters, `DREAM_ISO_BUILDTEST`, screen-relative axis rewriting, and the shader edits.

## Decisions

| Topic | Decision |
|---|---|
| Camera | Perspective, pitch 50°, yaw 45°, distance 18 m, FOV 35°, near 1, far distance + 80. Fixed, with no rotation or zoom. Orthographic is not included. |
| First and third person | Unreachable in play: no C key, no checkbox, and `GameSettings` ignores `camera_view` from config. Keep the enum values, rig branches, viewmodel stack, crosshair code and their tests, because deleting them costs more than it saves. Delete the C key, the `ToggleCameraViewMode` method, and the Third Person, FOV and Mouse Sensitivity controls in Settings. `MouseSensitivity` stays as a value because the capture `fight` step uses it. |
| Fog | Measured from the focus, via `Render.FogOrigin`. No shader change, because `uCameraPosition` is used only by fog. |
| Map edge | In iso, clear to `sky.HorizonColor * 0.25` and skip `SkyRenderer.Draw`. |
| Y smoothing | Exponential, τ 0.18 s. Snap if the jump exceeds 4 m. |
| Left click on ground | Path to that cell in any interaction mode. Holding the button repaths toward the cursor at most 4 times a second. |
| Left click on enemy | Walk into range (eye to combat anchor ≤ 3.2 m), stop, face the anchor and hold primary until the enemy is gone or a new click arrives. Sends `RequestedMode = Combat` if the mode isn't Combat already. |
| Right click, Combat mode | Face the cursor point and hold primary while the button is down. This attacks or breaks cubes in range without moving. |
| Right click, Building mode | Picks a cell. If the eye is more than 2.5 m from the cell centre, walk until it is ≤ 2.4 m. Then face the cell centre and press primary for 1 tick. |
| Build protocol | **No change.** Facing the cursor is enough because the server target is eye + facing × 2 m, and facing may be pitched. Limit: closer than about 1.5 m, the cube lands one cell beyond the cursor. Accepted. |
| Keys | W, A, S, D, mouse look and left-as-primary are removed. Keep Space (jump), Left Ctrl (noclip descend), F, 0, 1, 2, V, Esc and F3. |
| Noclip | Clicks are ignored while noclip is on. Noclip is dev-only and now has no horizontal movement, except the capture `fight` step. |
| Facing | Facing the movement direction replaces mouse look. It is set each tick while walking, snaps with no turn smoothing, and is kept when idle. Axes stay `(Forward = 1, Strafe = 0)`, so the spike's axis rewriting and its `HasSameMovement` caveat go away. |
| Cursor | `RuntimeState.DesiredCursorState` is always Normal. `ClassicCursorPolicy` gives Hidden when the classic cursor is active, Settings is closed and the window is focused. The sprite cursor is always on. |
| V overlay | No longer pauses play: `AllowGameplayInput => Mode != SettingsMenu`. Clicks outside the windows and bar still move the character. Settings and death cancel the current goal. |
| `CursorChangeLookHoldoff` | Delete the class, its field, its call and its tests. |
| `GameplayInputResumeFilter` | Unchanged. It still guards Space and the capture `fight` step's primary. |
| HUD click gate | A world press counts only if the press edge happens over no HUD. See `WorldPointerState` below. A drag that starts on the HUD can never become a world click. |
| Occlusion | Local-player silhouette only, drawn first with `DepthFunc Greater`, no depth write, tint `(0.5, 2, 6)`. |
| Protocol, server, goldens | No changes. No protocol version bump. |
| Excluded | Blob shadow, MSAA, destination marker, turn smoothing, melee line of sight, ghost preview, enemy pathing, stall scenarios, performance work. |

## Design and seams

### Tick flow in `PlayScene.Update.cs`
1. **Frame level:** `_clickMove.OnFrame(pointer, allowGameplayInput)` handles press edges, picks and paths. If it has a walking goal, the raw frame gets `ForwardAxis = 1`.
2. **Tick level, first line of `RunPredictionTick`:** `command = _clickMove.SteerTick(command);`
3. `SteerTick` calls `Player.SetFacing(direction)` and sets the jump pulse, primary and mode. `QueueRemoteCommandFrame` then relays `Camera.Front`, so client and server see one facing.

**Why the frame marks axis 1 first:** `ClientCommandTickStream.NextCommand` zeroes axes during a send stall. `SteerTick` therefore returns unchanged when `ForwardAxis == 0` and has no walking goal to apply, which keeps the stall rule intact and pauses stuck detection during a stall. An attack or build action with no movement must still run with axis 0.

### Navigation (client only, `dream/Client/Navigation/`)
- **Node:** `(x, level, z)`. Level = body cell, so a body centre at y 1.5 is level 1.
- **Blocked cell:** any `Map.TryGetGeometryCellAtCell` record, partial cells included. Rubble is ignored.
- **Support cell:** a record that is solid, `SubdivisionState == null`, and shape `SolidCube`.
- **Standable:** cell free, and the cell below is support. Body is 1 m tall, so one free cell.
- **Moves:**
  - 8-way on the same level. Diagonals need both orthogonal neighbours standable (no corner cutting).
  - Orthogonal step up by 1, needing the cell above the origin free.
  - Orthogonal drop by any height.
- **Costs:** 10, 14, jump + 10, integer.
- **Search:** `PriorityQueue<int,int>`, buffers reused, `MaxExpansions = 20000`, map rebuilt into a live query (no cached grid).
- **Unreachable goal:** track the expanded node with the smallest horizontal distance to the goal, then smallest level difference. Return the path to it. Horizontal comes first so a click that lands on the floating plate (side finding in the feasibility report) still walks underneath it.
- **Waypoints:** cell centres with no smoothing, so a 1-wide door is entered centred. Skip the start-cell centre on a normal click, include it on a stuck replan.
- **Follower, per 64 Hz tick:**
  - Advance when horizontal distance < 0.12 m. Tick step is 0.094 m at 6 m/s.
  - Next waypoint higher and within 1.1 m: pulse jump for 1 tick, then 8 ticks false, because the motor edge-detects and ignores jumps in the air.
  - If the next cell is no longer standable: replan now.
  - Stuck: under 0.05 m progress over 32 ticks. First a jump pulse, then replan. After 3 failed replans, stop.

### World pick (`dream/Client/Input/WorldCursorPicker.cs`)
- Ray from the inverse view-projection of last frame's camera.
- Enemy first: `Enemies.TryResolveHit(origin, dir, 90f, hitRadius: 1f)`.
- Then march at 0.05 m to the first blocked cell, up to 60 m, and return the last empty cell.
- Runs on presses and held-repaths only.

### HUD gate (`Render.Hud.cs`)
- `interactive = windowFocused && !SettingsVisible && !WantCaptureMouse`; the scripted pointer uses `!SettingsVisible`.
- `_classicCursorActive = drew && !SettingsVisible`.
- In `BeginHudFrame`, ImGui always gets the real mouse, so the Build panel works during play. `MouseDrawCursor = !classicCursor`.
- `OverHud = WantCaptureMouse || router hot id set || dragging || modal || ClassicHudHitTest(layout, inventoryOpen, position)`.
- The hit test covers `Bar` always, and `Inventory` and `Character` when open.

## C0 contracts (one commit, before the workstreams)
Needed because the input workstream compiles against nav, and `GameSettings` plus the C key cross workstreams. Do it directly on `integrate/isometric` from master, then every worktree resets to that tip.

- `CameraViewMode.Isometric`. `GameSettings` default and forced Isometric, `ToggleCameraViewMode` removed, C key removed from `PlayScene.Update.cs`, `camera_view` parses and serialises `isometric`.
- Tests: fix `GameSettingsTests` for default, config and toggle. Check `DesktopClientTextConfigTests` lines 64 and 92 against the same change.
- `public Camera? LastWorldCamera { get; set; }` and `public Vector3? FogOrigin { get; set; }` on `Render`, both inert for now.
- Stubs throwing `NotImplementedException`, in `dream/Client/Navigation/`:

```csharp
public readonly record struct NavCell(int X, int Level, int Z)
{
    public Vector3 Centre => new(X + 0.5f, Level + 0.5f, Z + 0.5f);
    public static NavCell FromBody(Vector3 bodyCentre);
}
public interface INavWorld
{
    int Width { get; } int Height { get; } int Depth { get; }
    bool IsBlocked(int x, int y, int z);
    bool IsSupport(int x, int y, int z);
}
public sealed class MapNavWorld(Map map) : INavWorld;
public sealed class GridPathfinder
{
    public const int MaxExpansions = 20000;
    // path is filled start..end inclusive. False only if no standable start exists.
    public bool TryFindPath(INavWorld world, NavCell start, NavCell goal,
                            List<NavCell> path, out bool reachedGoal);
}
public readonly record struct NavStep(Vector3 Direction, bool Jump, bool Arrived, bool NeedsReplan);
public sealed class PathFollower
{
    public bool HasPath { get; }
    public void SetPath(IReadOnlyList<NavCell> path, bool viaStartCentre);
    public void Clear();
    public NavStep Step(Vector3 bodyCentre, INavWorld world); // once per 64 Hz tick
}
```

## Workstreams

### 1. `nav` (no dependencies)
**Owns:** `dream/Client/Navigation/*` (all), `dream.Tests/Client/Navigation/*`.

**Steps:** fill `MapNavWorld`, then `GridPathfinder`, then `PathFollower`; then write the tests.

**Acceptance:**
- Path around a 2-high wall.
- Step up on a 1-high wall.
- Drops work, and a 1-wide door works.
- No corner cuts.
- A walled-in goal returns the nearest cell with `reachedGoal == false`.
- Stuck replan fires, and the attempt gives up after 3.
- Real-physics walk test passes (below).

**Filter:** `FullyQualifiedName~Client.Navigation`

### 2. `iso-view`
**Owns:**
- `dream/Client/Presentation/IsometricView.cs` (new)
- `dream/Client/World/ClientWorldSession.cs`
- `dream/Client/Scenes/PlayScene.Render.cs`
- `dream/Rendering/Core/Render.cs`
- `dream/Rendering/Core/Render.Lighting.cs`
- `dream/Rendering/Core/Render.HudPanels.cs`
- `dream/Rendering/Actors/LocalPlayerRenderer.cs`
- `dream.Tests/Client/Presentation/IsometricViewTests.cs` (new)

**Steps:**
1. `IsometricView`: constants, `FocusToCamera`, `ResolveInto(Vector3 focus, Camera target)`, `CreateProjection(float aspect)`, and a small stateful `IsometricFocus.Advance(Vector3 eye, float dt)`.
2. `CapturePresentationState` gains `float frameSeconds = 0f`, branches on Isometric, and exposes `LastCameraFocus`.
3. `PlayScene.Render`:
   - Pass `args.Time`.
   - Set `_render.FogOrigin` and `_render.LastWorldCamera` before `DrawWorld`.
   - Apply the dim clear and skip the sky pass.
   - Change the local-player draw guard from `== ThirdPerson` to `!= FirstPerson`.
4. `UpdateProjectionForSettings` uses the iso projection when in that mode.
5. `UploadFrameLighting` uses `FogOrigin ?? camera.Position`.
6. Remove the three Settings controls.
7. Add the silhouette.

**Acceptance:**
- World renders in the fixed view following the player.
- No milky fog, and no bright void past the map edge.
- Jumps don't bob the camera.
- Player is visible behind a wall.

**Filter:** `FullyQualifiedName~IsometricView|FullyQualifiedName~ThirdPersonCameraRig|FullyQualifiedName~PresentationCameraScratch|FullyQualifiedName~FrameLightingContract`

### 3. `click-input`
**Owns:**
- `dream/Client/Input/PlayerController.cs`
- `dream/Client/Input/ClickMoveController.cs` (new)
- `dream/Client/Input/WorldCursorPicker.cs` (new)
- `dream/Client/Input/CursorChangeLookHoldoff.cs` (delete)
- `dream/Client/Scenes/PlayScene.Update.cs`
- `dream/Client/Scenes/PlayScene.cs`
- `dream/Client/Scenes/PlayScene.Capture.cs`
- `dream/Client/Scenes/HudCapturePointerPlan.cs`
- `dream/Application/RuntimeState.cs`
- `dream/Rendering/Core/Render.Hud.cs`
- `dream/Rendering/Ui/ClassicCursorPolicy.cs`
- `dream/Rendering/Ui/ClassicHudHitTest.cs` (new)
- `docs/configuration.md`
- Tests: `GameSettingsTests`, `CursorChangeLookHoldoffTests` (delete), the two classic HUD test files that call `Decide`, and new `ClickMoveControllerTests` and `ClassicHudHitTestTests`.

**Steps:**
1. `CaptureCommandFrame` loses WASD, mouse deltas and the left button. `CreateCommandFrame` is unchanged.
2. Render exposes `WorldPointerState SampleWorldPointer(MouseState, GameSettings, bool windowFocused)`, with `record struct WorldPointerState(Vector2 Position, bool LeftDown, bool RightDown, bool OverHud)`. It reads the scripted pointer when active, so the script takes the same path as a hand. The pointer is read in Update and reflects the previous Render's hover state.
3. Add `ClickMoveController`, with goals `None | Walk(cell) | Attack(enemy) | Build(cell)`. Attack repaths when the enemy moves more than 1 m, at most every 0.5 s. Dead or inactive enemy clears the goal.
4. Cursor, overlay and HUD gate changes.
5. Crosshair guard for Isometric in `Render.Hud.cs`.
6. Capture verb `walkto` and an updated verb list in `docs/configuration.md`.

**Capture verb `walkto:<x>:<z>:<maxFrames>`:**
- While the controller is idle, projects the target and clicks it through `HudCapturePointerPlan.Click`.
- If off-screen, clicks the point on the screen line from player to target, inset 15% and above the bar.
- Ends within 1.5 m of the target or at the timeout.
- Logs `[hud-capture] walkto x,z start=... end=... clicks=N pathCells=M reached=bool`.
- Add the player's position to the `log` verb.

**Acceptance:**
- Ground click walks, enemy click kills.
- Right click builds and breaks.
- HUD and window clicks never set a goal.
- Sprite cursor always on.
- Moving with the inventory open works.
- WASD does nothing.
- The `fight` capture step still works.

**Filter:** `FullyQualifiedName~ClickMove|FullyQualifiedName~ClassicHud|FullyQualifiedName~PlayerController|FullyQualifiedName~GameSettings|FullyQualifiedName~GameplayInputResumeFilter|FullyQualifiedName~DeadInputPolicy`

## Merge order
C0 is the base. Then `nav` (new files, fills the stubs), then `iso-view`, then `click-input` (the only one that needs both). `click-input` cannot do its real-game proof on its own branch, because `LastWorldCamera` is only assigned by `iso-view`. That proof runs on the integration branch.

## Tests
- **Nav:** cases listed in workstream 1.
- **Physics walk:** real `ClientWorldSession`, with static cubes from `Map.TryAddCube(isStatic: true)` and a loop of `Step`, `SetFacing`, `AdvancePredictionTick`. Harness pattern is in `PresentationCameraScratchTests`. Covers the wall detour, the 1-high jump, the 1-wide door, and a cube added mid-walk forcing a replan.
- **`IsometricView`:** camera offset, facing, near/far, focus Y smoothing and snap.
- **`ClickMoveController`, with fake `INavWorld`:**
  - HUD press sets no goal.
  - A press that starts on the HUD and is released on the world sets no goal.
  - `SteerTick` leaves a stall-zeroed walk alone.
  - Attack holds primary only in range.
  - Mode request is sent once.
  - Build pulses primary for exactly 1 tick.
  - Cancels on `allowGameplayInput == false`.
- **`ClassicHudHitTest`:** bar, windows open and closed. `ClassicCursorPolicy` table updated.
- **Full suite:** the integrator runs it once. Workstreams run filters only. Include tick equivalence and state checksum in that run as the check that goldens did not move.

**Real-game proof (on the integration branch):**
- Env: `DREAM_AUTO_CONNECT=1`, no `DREAM_SERVER_HOST`, `DREAM_MAX_FPS=60`, `DREAM_HUD_CAPTURE=<G: dir>`, and `DREAM_IDENTITY_FILE=<G: path>` for a fresh character at spawn (5, 5). The default identity path is on C:. `config/player-profiles.db` lives in the output folder, so a fresh worktree output means a clean world.
- Script: `DREAM_HUD_CAPTURE_SCRIPT=play,walkto:13:17:1500,snap,walkto:25:17:1500,snap,log`.
- **The proving step is `walkto:25:17:1500`.** The pyramid occupies about x 14..23, z 12..21 and is 4.35 m tall, so the straight line from (13, 17) to (25, 17) passes through blocked cells.
- Pass: `reached=true`, `end` within 1.5 m of (25, 17), path noticeably longer than the 12-cell straight line (`pathCells` ≥ 14 given diagonals; confirm the actual count), and the frames show the character on the far side.
- Then repeat with `fight:600,snap` for a combat regression, and inspect one `DREAM_TIME_OF_DAY=night` frame.

## Standing rules
- New branch `integrate/isometric` from master at 7264b34.
- Worktrees start from master, so reset each to the C0 tip before work.
- Nothing pushed. Nothing merged to master.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Nothing bound to all interfaces. Don't click any firewall prompt.
- Don't stop processes you didn't start.
- Minimal real game windows, and never take focus.
- C: is nearly full, so all output goes on G:. Tests need an absolute `BaseOutputPath` inside the worktree.
- No source-game names anywhere: code, docs or commits.

## Risks
- **1-wide doors and body width:** the 1 m body has no clearance. The spike saw corrections at 8 cm offset. Centred waypoints address this, but a fractional offset after a correction may brush a jamb. Stuck replan via the start-cell centre is the fallback.
- **Jump timing:** auto-jump constants are calculated, not measured. Jump height is about 1.27 m, so a 1-step clears, but the 1.1 m trigger may need tuning.
- **Network stalls:** axes are constant, so `HasSameMovement` matches across a turn. A path that turns during a stall can produce a correction. Stall scenarios are out of scope.
- **Reduced view range:** at 18 m the ground shown is about ±7.4 m vertically and ±10 m horizontally at 16:9, by calculation. Long trips need several clicks or a held button.
- **Inventory no longer pausing:** any other code assuming the overlay stops play will now see gameplay input. Only the `PlayScene.Update.cs` uses were found.
- **Building precision:** placement is imprecise when very close, as stated in the decisions table.
- **Melee through walls:** there is no line-of-sight check, so a clicked enemy behind a wall can be hit from range. Existing behaviour.

## Could not establish
- **`(13, 17)` and `(25, 17)` standable:** inferred from generator ranges only, and boulder and rubble sculptures were not located. If either fails, shift the target by a cell or two and keep the line through the pyramid.
- **`pathCells` value:** the exact length is unknown until the first run, so the threshold above needs confirming there.
- **Frame pacing:** the pointer's 1-frame press needs one Update per Render. `Game.cs` comments imply update and render run together, but this was not traced fully.
- **Night fog and edge colour:** only `DayFogStart` 18 was read, not the night fog values.
- **Feel:** camera comfort, snap turning and click responsiveness are unproven until someone plays it.
