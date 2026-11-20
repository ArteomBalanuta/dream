# Tactical camera: edge pan, rotation and zoom-to-cursor (plan)

Status: design, not started. Base: `master` at `d3c78ef1`.

## Overview

The play camera is today a fixed rig: `IsometricView` puts a perspective camera at a constant offset
(pitch 50°, yaw 45°, 18 m, FOV 35°) from `IsometricFocus`, which is the player's eye with Y smoothed. It has no
pan, rotation or zoom.

This plan replaces it with a tactical rig that keeps exactly today's view as its default pose and adds:

1. **Edge pan**: the cursor in a margin along the window edge pans the view target over the ground, with
   acceleration and deceleration, speed scaled by zoom, and a leash to the player. Arrow keys do the same.
2. **Rotation**: middle-mouse drag rotates yaw around the view target (free and smooth, optional snap on
   release); `,` and `.` rotate by a snap step. Panning and WASD stay screen-relative at every yaw.
3. **Zoom**: the wheel changes the camera distance exponentially between bounds, smoothed, anchored on the
   world point under the cursor so that point stays under the same pixel.

Nothing changes on the wire: the camera is client presentation only. WASD already reaches the server as
forward/strafe relative to the facing, so only the client's screen basis changes.

## What exists and what it constrains

| Fact (file) | Consequence |
| --- | --- |
| `IsometricView` constants and `FocusToCamera` are static (`Client/Presentation/IsometricView.cs`) | Become the default pose. Existing tests and golden scenes rely on them; the default pose must reproduce them bit for bit. |
| `ClientWorldSession.CapturePresentationState` builds the camera from `IsometricFocus` and stores `LastCameraFocus` (`Client/World/ClientWorldSession.cs:226-231`) | The rig pose enters here. `LastCameraFocus` is split in two: the player focus and the view target. |
| War mist reveal and draw use `LastCameraFocus` (`PlayScene.WarMist.cs:54,76`) | Must stay on the **player** focus, or panning scouts through the mist. |
| `FogOrigin = LastCameraFocus` (`PlayScene.Render.cs:149`) | Distance fog follows the **view target**, so a panned-to area is not fogged. |
| `PlayerController.Project` uses the static `FocusToCamera` for the WASD basis (`Client/Input/PlayerController.cs:139`) | Takes the rig's current ground basis instead. |
| `SunShadowVolume.ViewForwardGround` is a static from the fixed yaw (`Rendering/Shadows/SunShadowVolume.cs:111`) | Becomes per frame from the rig yaw; the receiver centre follows the view target. |
| `IsometricView.FarPlane = Distance + 80` and the projection is rebuilt only on resize (`Render.cs:97-119`) | Far plane is derived from the current distance; projection rebuilt when it changes by more than a step. |
| Geometry export is centred on the camera position with radius 48 (`ClientWorldSession.cs:241`, `WorldVisibility.MaxRenderDistance`) | Centre moves to the view target; a test proves the footprint at max zoom fits the radius. |
| Hover pick, cursor aim and click-to-move build rays from `Render.LastWorldCamera` and `ProjectionMatrix` (`WorldCursorPicker.TryBuildRay`) | Follow the rig automatically. The `aimcheck` capture line stays valid at any yaw. |
| `WorldPointerState.OverHud` covers ImGui `WantCaptureMouse`, settings, classic HUD hit test (`Render.Hud.cs:126-158`) | This is the UI-blocking signal for the wheel. ImGui is not fed the wheel today; this plan does not change that. |
| Cursor is `CursorState.Hidden` with a sprite cursor (`ClassicCursorPolicy`, `Game.WithMenuCursor`); OpenTK 4.9.4 has `CursorState.Confined` | GLFW's confined state shows the OS cursor, which fights the sprite cursor. Confinement uses Win32 `ClipCursor` behind an interface instead. |
| Audio listener uses `presentation.Camera.Right` (`PlayScene.Audio.cs:86`) | Rotates with the camera; no change. |
| Keys in use: W A S D Space Shift Ctrl F Z G B Q R E V 0-6 Esc F3 Tab | Free for the camera: arrows (pan), `,` `.` (rotate step), `Home` (recentre), `Y` (follow/free). |
| `UiCursorKind.Grab` exists | Shown while a rotation drag is held. |
| The capture harness drives a scripted pointer and never takes focus; `walkto` aims at the screen edge | Camera input reads only the real mouse and the real window focus, so captures never edge-pan. New `cam:` steps stage the pose. |

## Design

### Pose and state

```
TacticalCameraPose(Vector3 Target, float YawDegrees, float PitchDegrees, float Distance)
  FocusToCamera = IsometricView.FocusToCameraFor(yaw, pitch, distance)
  Position      = Target + FocusToCamera
  GroundForward = normalize(flat(-FocusToCamera))     // screen up on the ground
  GroundRight   = normalize(cross(GroundForward, +Y))  // screen right on the ground
```

The rig holds a **goal** (pan offset, yaw, distance) and a **current** pose that follows the goal. Only the current
pose is rendered. Pitch stays 50°; a pitch-by-zoom curve is out of scope.

Target:

- `Follow` (default): `Target = playerFocus + panOffset`. With zero offset this is today's camera.
- `Free` (`Y` toggles): the target stays fixed in the world while the player walks.
- Both: target Y is the player focus Y (keeps `IsometricFocus` jump smoothing), and
  `|Target.xz - player.xz| <= LeashMetres`. `Home` sets the offset to zero (smoothed); a second `Home` within 0.4 s
  also resets yaw and distance to the defaults.
- `ReturnToPlayerSeconds` (default 0 = off): after that long with no pan input, the offset decays to zero.

The leash exists because war mist, distance fog, the geometry export and the server's replication are all centred
on the player. The leash default is derived in code so the view footprint at max zoom stays inside the export
radius (see Derived limits).

### Time

- The rig advances once per **rendered** frame with the unscaled frame time (`args.Time`), clamped to 0.1 s so a hitch
  does not jump the view. It never reads the simulation clock, the world clock or the fixed tick, so pause menus,
  stalls and any future time dilation do not affect it.
- All smoothing is exponential, `k = 1 - exp(-dt / tau)`, which is frame-rate independent. Pan velocity integrates
  with that `k` and `offset += v * dt`.
- The input sampler accumulates between renders (wheel notches and drag pixels are summed; edge pan is a level), and
  the rig consumes the accumulated frame. Different update and render rates cannot double-count.

### Edge pan

Edge field, per axis, in window client pixels with width `W` and margin `m = EdgeMarginPixels * uiLayoutScale`:

```
ex = -ramp((m - x) / m)            if x < m
ex = +ramp((x - (W - 1 - m)) / m)  if x > W - 1 - m
ramp(t) = smoothstep(0, 1, clamp(t, 0, 1));  (ex, ey) clamped to length 1.  ey is screen up (+ at the top edge).
```

Goal velocity `v* = (GroundRight * e.x + GroundForward * e.y) * PanSpeed * (currentDistance / DefaultDistance)`.
`v` approaches `v*` with `tau = PanAccelSeconds` when speeding up and `PanDecelSeconds` when slowing down. Arrow keys
add a unit vector in the same space at the same speed (sum clamped to length 1).

The edge field outputs zero unless all of these hold:

1. The window is focused and not minimised, and the camera input gate is open (see Input priority).
2. The cursor is inside the client rectangle: tracked by `MouseEnter`/`MouseLeave`, and a position outside `[0, W) x
   [0, H)` counts as outside. When the cursor crosses to a second monitor, `MouseLeave` fires and the pan stops
   (decelerating), instead of the last in-margin position panning forever.
3. The field has been non-zero for `EdgeDwellSeconds` (0.12 s) without a break. A sweep through the margin toward
   another monitor or the taskbar does not pan. Dwell is 0 while the cursor is confined, since it cannot leave.
4. At least `EdgeFocusGraceSeconds` (0.25 s) since focus was gained or the cursor entered: the click that refocuses
   the window often lands in the margin.
5. No rotation drag is held, and no HUD drag (inventory item, ImGui active drag) is in flight. Hovering the HUD does
   **not** block edge pan: the bottom margin lies inside the action bar and must still pan.
6. The real mouse, not the capture pointer: with a capture script running, the field is zero.

Cursor confinement (`ConfineMode`):

- `Off`: no confinement; rules 2 and 3 handle multi-monitor.
- `FullscreenOnly` (default): confine when the window is fullscreen or borderless covering a monitor.
- `Always`: confine whenever play has focus.

Confinement is Win32 `ClipCursor(clientRectInScreenCoords)` behind `ICursorConfiner` (no-op off Windows). It is
applied on focus gained, window move or resize, and play resumed; released on focus lost, minimise, pause menu or
settings open, loading screen, scene exit and dispose. Windows drops a clip on focus change, so the policy
re-applies rather than assuming it holds. The pause menu always releases it: Esc is the escape hatch.

### Rotation

- Middle press **over the world** (not `OverHud`), focused, gate open: starts a drag. The decision is latched at the
  press and holds until release, wherever the cursor goes (the press/release identity rule). A middle press that
  began over the HUD never rotates.
- While held: `goalYaw += dx * RotateDegreesPerPixel`; edge pan off; the sprite cursor shows `Grab`.
- On release, if `SnapOnDragRelease`, the goal yaw rounds to the nearest `RotateSnapStepDegrees`, phased so the
  default 45° is on the grid. Focus loss cancels the drag as a release.
- `,` / `.` move the goal yaw by `-step` / `+step` from the current goal rounded to the grid.
- The current yaw follows the goal on the shortest arc (wrapped to (-180, 180]) with `RotateSmoothingSeconds`.
- Rotation pivots on the view target. The pan offset is in world space, so the view centre does not move while
  rotating.
- Pan, WASD and the shadow receiver use the **current** (smoothed) yaw's basis, so screen up stays screen up during a
  rotation.

### Zoom and zoom-to-cursor

- `goalDistance = clamp(goalDistance * ZoomStepRatio^(-notches), MinDistance, MaxDistance)`, sign flipped by
  `InvertZoom`. Notches are floats, so touchpads give fractional steps. The current distance follows the goal
  in log space with `ZoomSmoothingSeconds`, so in and out feel symmetric.
- The wheel goes to the world only when the pointer is not `OverHud` and the gate is open. Over UI it is dropped,
  not converted into zoom.

Zoom-to-cursor. When a zoom gesture starts (the first notch after 0.3 s without one), the anchor `A` is latched:
the enemy anchor if the hover probe has one, else the `WorldCursorPicker.Pick` ground point, else the cursor ray
cut with the plane `y = Target.y`. A stays latched until the distance settles, so the voxel march cannot hop
between heights mid-zoom.

Each frame the current distance moves from `d0` to `d1`. Keep the yaw `psi` and pitch, keep `Target.y = h`, and
solve for the target that keeps `A` under the same pixel. The orientation is unchanged, so the ray from the camera
through that pixel keeps its direction `r = normalize(C0 - A)`, where `C0` is last frame's camera:

```
u  = FocusToCameraFor(psi, pitch, 1)          // unit, target -> camera
C1 = A + r*t  and  C1 = T1 + u*d1,  T1.y = h
t  = (h + u.y*d1 - A.y) / r.y                 // r.y > 0: the camera is above the anchor
T1.xz = A.xz + r.xz*t - u.xz*d1
```

This holds for an anchor at any height, so a raised enemy or block stays put. Guards: if `r.y < 1e-3`, or the yaw is
still turning (`|currentYaw - goalYaw| > 0.5°`), zoom about the centre for that frame. The leash clamp runs after
the solve. At the leash limit the clamp wins, so the anchor drifts and the target stays inside the leash.

### Input priority and gate

The camera input gate is open when the window is focused, play is active, the loading gate does not block,
`PauseMenuVisible` and `SettingsVisible` are false, and no text field has focus (keys only; chat typing still lets
the mouse zoom). **Death does not close it**: a dead player may look around.

Per frame, highest first:

1. Gate closed: no new input. Smoothing keeps running, so motion settles instead of freezing.
2. A rotation drag is held: rotate only; the wheel still zooms (about the centre while turning, per the guard).
3. Wheel: `OverHud` goes to the UI, otherwise zoom.
4. Edge pan and arrow keys under the rules above.
5. `Home`, `Y`, `,`, `.`: edge-triggered on press, never auto-repeated per frame.

Left and right buttons keep their current meanings (aim/attack, click-to-move). The camera never consumes them.

### Derived limits (derived in code, each pinned by a test)

- `MinDistance` 10 m; `MaxDistance` is derived from the shadow ortho side (`ShadowSettings.OrthoSide`) and the FOV, so
  the visible ground footprint at max zoom stays inside the shadow volume. It is capped at 30 m. A test checks the
  four view-corner ground hits against the receiver box.
- `FarPlaneFor(d) = d + 80` (today's relation, at the current distance).
- `LeashMetres = MaxRenderDistance - footprintRadius(MaxDistance)`, floored at 8 m: the export, centred on the target,
  still covers the footprint at the leash limit. The architect must confirm the server replication radius for
  enemies and remote players; if it is smaller, it also bounds the leash.

### Settings

`TacticalCameraSettings` (record with defaults and `Sanitized()`):

| Setting | Default | Range | In menu |
| --- | --- | --- | --- |
| `EdgePanEnabled` | true | | yes |
| `EdgeMarginPixels` | 10 (x UI layout scale) | 2-48 | yes |
| `PanSpeed` (m/s at default distance) | 14 | 4-40 | yes |
| `PanAccelSeconds` / `PanDecelSeconds` | 0.08 / 0.14 | 0-0.5 | no |
| `EdgeDwellSeconds` / `EdgeFocusGraceSeconds` | 0.12 / 0.25 | 0-1 | no |
| `ConfineMode` | `FullscreenOnly` | Off, FullscreenOnly, Always | yes |
| `AnchorMode` | `Follow` | Follow, Free | yes (also `Y`) |
| `ReturnToPlayerSeconds` | 0 (off) | 0-10 | yes |
| `RotateDegreesPerPixel` | 0.3 | 0.05-1.5 | yes |
| `RotateSnapStepDegrees` | 90 | 15, 30, 45, 90 | yes |
| `SnapOnDragRelease` | false | | yes |
| `RotateSmoothingSeconds` | 0.12 | 0-0.5 | no |
| `ZoomStepRatio` | 1.15 | 1.02-1.5 | yes (as speed) |
| `ZoomSmoothingSeconds` | 0.10 | 0-0.5 | no |
| `InvertZoom` | false | | yes |
| `DefaultYawDegrees` / `PitchDegrees` / `DefaultDistance` | 45 / 50 / 18 (from `IsometricView`) | fixed | no |

These persist in the desktop client config next to `UiScale` (`DesktopClientConfig`, text keys `camera_*`), are
exposed on `GameSettings.TacticalCamera`, and the "in menu" rows go into `MenuOptionRows`. Unknown or out-of-range
values are sanitised, never rejected.

### Capture steps (verification)

`cam:yaw:<deg>`, `cam:zoom:<metres>`, `cam:pan:<dx>:<dz>` (offset in metres), `cam:free` and `cam:follow`,
`cam:reset`. Each snaps the goal and current pose (no smoothing), so a frame is deterministic. `status` adds
`cam target=x,y,z yaw=.. dist=.. offset=.. mode=..`. Without `cam:` steps a capture is identical to today's.

## Contracts (frozen before the build)

New namespace `Dream.Client.CameraControl` (`dream/Client/CameraControl/`).

```csharp
public enum CameraAnchorMode : byte { Follow, Free }
public enum CameraConfineMode : byte { Off, FullscreenOnly, Always }

public sealed record TacticalCameraSettings { /* properties in the Settings table */
    public static TacticalCameraSettings Default { get; }
    public TacticalCameraSettings Sanitized(); }

public readonly record struct TacticalCameraPose(Vector3 Target, float YawDegrees, float PitchDegrees, float Distance) {
    public Vector3 FocusToCamera { get; } public Vector3 Position { get; }
    public Vector3 GroundForward { get; } public Vector3 GroundRight { get; }
    public static TacticalCameraPose Default(Vector3 target);
    public void ResolveInto(Camera camera); }

// Everything the camera reads from input this rendered frame (accumulated since the last one).
public readonly record struct CameraInputFrame(
    Vector2 EdgePan,           // screen space, x right, y up, |v| <= 1, already gated
    Vector2 KeyPan,            // arrows, same space
    float RotateDragPixels,    // horizontal pixels while a world-started middle drag is held
    bool RotateDragActive, bool RotateDragReleased,
    int RotateSteps,           // ',' = -1, '.' = +1, summed
    float ZoomNotches,         // + = in, already gated by OverHud
    bool RecentrePressed, bool ToggleAnchorPressed) {
    public static CameraInputFrame None { get; } }

public interface ICursorConfiner { bool IsConfined { get; } void Confine(Box2i clientRectScreen); void Release(); }

public sealed class TacticalCameraRig {                       // stub bodies throw until task A
    public TacticalCameraRig(TacticalCameraSettings settings);
    public TacticalCameraSettings Settings { get; set; }
    public TacticalCameraPose Pose { get; }                   // current, what renders
    public CameraAnchorMode AnchorMode { get; }
    public Vector3 PanOffset { get; }
    public bool ZoomGestureActive { get; }                    // true from a gesture's first notch until settled
    public void LatchZoomAnchor(Vector3? worldAnchor);        // called once per gesture when it starts
    public TacticalCameraPose Advance(Vector3 playerFocus, in CameraInputFrame input, float realDeltaSeconds);
    public void SnapTo(Vector3 playerFocus, float? yawDegrees, float? distance, Vector2? panOffsetXz, CameraAnchorMode? mode);
    public void Reset(Vector3 playerFocus); }

public static class ZoomAnchorSolver {                        // stub until task A
    public static bool TrySolveTarget(Vector3 anchor, Vector3 previousCameraPosition,
        float yawDegrees, float pitchDegrees, float distance, float targetY, out Vector3 target); }

public sealed class CameraInputSampler {                      // stub until task B
    public CameraInputSampler(TacticalCameraSettings settings);
    public TacticalCameraSettings Settings { get; set; }
    public void OnFocusChanged(bool focused, double nowSeconds);
    public void OnCursorEnterLeave(bool inside, double nowSeconds);
    public void Collect(MouseState mouse, KeyboardState keys, Vector2i clientSize, float uiLayoutScale,
        in WorldPointerState pointer, CameraGate gate, double nowSeconds); // called per Update
    public CameraInputFrame Take();                           // per Render; resets accumulators
    public bool RotateDragHeld { get; } }

public readonly record struct CameraGate(bool Open, bool KeysOpen, bool Confined, bool HudDragActive, bool ScriptedPointer);

public static class CursorConfinePolicy {                     // stub until task B
    public static bool ShouldConfine(CameraConfineMode mode, bool focused, bool minimised, bool fullscreenLike,
        bool pauseOrSettingsOpen, bool loading, bool inPlay); }
```

Changes to `IsometricView`, made complete in the freeze (they are tiny and shared): add
`FocusToCameraFor(float yawDeg, float pitchDeg, float distance)`, `FarPlaneFor(float distance)` and
`CreateProjection(float aspect, float distance)`. The existing constants, `FocusToCamera` and `ResolveInto` stay as
the default pose. A test pins `TacticalCameraPose.Default(f).ResolveInto` equal to `IsometricView.ResolveInto(f)`.

## Workstreams (3, built in parallel, disjoint files)

The four concerns map to three workstreams. Zoom-to-cursor is pure maths on the pose type, so it sits with the rig.
Settings persistence sits with the world integration, which is where the settings are read.

| WS | Owns | Builds | Tests (new files under `dream.Tests/Client/CameraControl/` unless noted) |
| --- | --- | --- | --- |
| **A: rig maths** | `Client/CameraControl/TacticalCameraRig.cs` (bodies), `Client/CameraControl/ZoomAnchorSolver.cs` (bodies) | goal/current state, exponential smoothing, yaw wrap and snap grid, log-space zoom, pan velocity accel/decel, follow/free, leash clamp, return-to-player, zoom gesture latch, closed-form anchor solve, guards | `TacticalCameraRigTests`: default pose equals today's; frame-rate independence (same input for 1 s at 30/60/144/240 fps within 1e-3); 10 s with no input = no drift; dt clamp; leash; shortest-arc yaw; snap grid phase; pan basis at yaw 45/135/-90 (screen up is GroundForward); speed scales with distance. `ZoomAnchorSolverTests`: projected pixel of the anchor unchanged within 0.5 px at 1440x884 for anchors on the plane, 1 m above and 2 m below, zooming in and out; `r.y` guard; bound clamp |
| **B: input and cursor** | `Client/CameraControl/CameraInputSampler.cs` (bodies), `Client/CameraControl/EdgePanField.cs` (new), `Client/CameraControl/CursorConfinePolicy.cs` (bodies), `Client/CameraControl/Win32CursorConfiner.cs` (new, `ClipCursor`/`GetClientRect`/`ClientToScreen` P/Invoke), `Client/CameraControl/NullCursorConfiner.cs` (new) | edge field with margin, ramp, dwell, grace, inside tracking; middle-drag latch at press; wheel gating; key edges; accumulation across Updates; confine policy | `EdgePanFieldTests` (each edge, corners clamp to 1, centre 0, margin scales with UI scale); `CameraInputSamplerTests`: leave stops pan; out-of-rect position stops pan; dwell blocks a sweep; focus grace; scripted pointer gives zero; wheel over HUD gives 0 notches; drag started over HUD never rotates; drag latched when the cursor moves onto HUD; focus loss releases the drag; two Updates before one Render sum the wheel; key press counted once while held; gate closed gives `None`. `CursorConfinePolicyTests`: full truth table, pause always releases |
| **C: settings and world integration** | `Configuration/DesktopClientConfig.cs`, `Configuration/DesktopClientTextConfig.cs`, `Application/GameSettings.cs`, `Client/Menu/MenuOptionRows.cs`, `Client/World/ClientWorldSession.cs`, `Client/Input/PlayerController.cs`, `Rendering/Shadows/SunShadowVolume.cs`, `Client/Scenes/HudCapturePointerPlan.cs`, `Client/Scenes/PlayScene.Camera.cs` (new partial) | persistence and sanitising; menu rows; `CapturePresentationState(.., TacticalCameraPose pose)` with `LastPlayerFocus` and `LastViewTarget` (keeping `LastCameraFocus` as the player focus alias for the war mist structural test); export centred on the target; `Project(axes, facing, groundForward)`; shadow forward from a pose; `cam:` step parsing; `PlayScene.Camera.cs` holds the rig, sampler and confiner fields, `UpdateCameraInput()`, `AdvanceCamera(frameSeconds)` (anchor latch via the existing picker), `ApplyCaptureCamera(step)` | config round-trip and sanitise; menu rows present and bounded; `ClientWorldSession` with a rotated, zoomed, panned pose (camera at pose position, player focus unchanged); `PlayerControllerProjectTests` at four yaws (W is screen up); shadow forward at yaw 135; `cam:` parse tests; derived-limit tests (MaxDistance footprint inside shadow box; leash keeps footprint in export radius) |

`PlayScene.Camera.cs` is assigned to C even though `PlayScene.*` is normally a hot path: it is a new file, and no
other workstream touches it.

### Integration after merge (single owner, small edits in hot files)

1. `PlayScene.Update.cs`: call `UpdateCameraInput(keyboardState, mouseState, pointer)` after the pointer sample and
   pass `_runtime.CurrentGroundForward` into `ProjectKeyboardAxes`.
2. `PlayScene.Render.cs`: `AdvanceCamera(args.Time)` before `CapturePresentationState`, pass the pose, and set
   `FogOrigin = LastViewTarget`.
3. `PlayScene.WarMist.cs`: reveal and draw from `LastPlayerFocus`.
4. `PlayScene.Capture.cs`: dispatch `cam:` steps to `ApplyCaptureCamera`; add the `cam` part of the `status` line.
5. `Render.cs`: `SetIsometricDistance(float)` rebuilds the projection with `FarPlaneFor` when the distance moves by more
   than 0.25 m; the shadow pass takes the forward from the pose.
6. `Game.cs`: forward `FocusedChanged`, `MouseEnter`, `MouseLeave` and minimise to the sampler; apply and release the
   confiner per `CursorConfinePolicy` each frame (only on a change); `Grab` cursor kind while `RotateDragHeld`.
7. `docs/configuration.md`: camera settings, keys, `cam:` steps. Ledger work-log entry.

## Verification

- Task gates: build `dream.csproj` and `dream.Tests.csproj`, filtered tests of the workstream's own classes.
- Integration: full suite once (teed to a file); `tools/golden/run.ps1 -Scene all -FromSource` must pass **unchanged**
  (the default pose is today's); re-run the existing `IsometricViewTests`, `CursorAimParallaxTests`,
  `ClickSoakTests`, `WarMistStructuralTests`, shadow and culling tests.
- Captures (`DREAM_AUTO_CONNECT=1`, `-FromSource`), each looked at:
  1. `play,tod:12,snap`: identical to master.
  2. `...,cam:yaw:135,snap` and `cam:yaw:-45`: world rotated, HUD, nameplates and damage numbers in place, shadows
     covering the view.
  3. `...,cam:zoom:10,snap` / `cam:zoom:<max>,snap`: no shadow cut-off or far-plane clipping at the view edges.
  4. `...,cam:pan:12:0,snap`: war mist still centred on the player, panned area not fogged.
  5. `...,cam:yaw:135,keys:w:60,status`: the player walks screen-up.
  6. `...,dev:spawn:wolf:1:6,aimenemy,status` at yaw 135 and zoom 12: aim still lands (the `aimcheck` line).
- By hand (not automatable; the user or a manual pass): edge pan in windowed mode with a second monitor (cursor
  leaving stops the pan); confinement in borderless fullscreen and its release on Esc and alt-tab; wheel over the
  inventory does not zoom; middle-drag rotation feel and snap.

## Acceptance

1. With no camera input and the defaults, every golden scene and capture is pixel-identical to master.
2. Edge pan moves the view at `PanSpeed x distance/18` m/s after dwell, stops within `PanDecelSeconds x 5` after the
   cursor leaves the margin or the window, and never moves while the window is unfocused or the cursor is outside
   it unconfined.
3. Rotation by drag and by keys; at any yaw, W, edge-up and arrow-up all move toward screen up.
4. Zoom between bounds; the hovered anchor stays within 1 px during a settled-yaw zoom (solver test plus capture).
5. The wheel over any HUD panel never zooms; a middle drag started on the HUD never rotates.
6. Behaviour at 30, 60 and 144 fps is the same (rig tests); the pause menu freezes input but not settling.
7. War mist and exploration remain player-centred; no protocol bump; full suite green.

## Out of scope

Pitch control or a pitch-by-zoom curve; camera collision or occlusion fade; minimap click-to-jump; ImGui wheel
scrolling; gamepad; first and third person (unreachable); per-binding key remapping UI; changing server replication
radius.

## Open questions

1. The server's replication radius for enemies and remote players: if smaller than the derived leash, it bounds it.
2. Snap step default 90° (keeps the diagonal look) or 45° (axis-aligned views show flat block faces): 90° assumed.
3. Should `Free` be the default for players used to RTS controls? `Follow` assumed, because it is today's behaviour.
