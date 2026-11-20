# 2026-11-20: tactical camera (edge pan, rotation, zoom-to-cursor)

Plan: `docs/ledger/plans/2026-11-20-tactical-camera-plan.md`. Branch `integrate/tactical-camera` from `master` `d3c78ef1`; not merged.

## What was built

- `Dream.Client.CameraControl`: `TacticalCameraRig` (goal/current pose, exponential smoothing on real frame time capped
  at 0.1 s, log-space zoom, shortest-arc yaw on a snap grid through 45°, pan velocity with accel/decel, Follow/Free,
  leash, Home recentre and double-tap reset, zoom-to-cursor via the closed-form `ZoomAnchorSolver`, solved in the
  player's frame in Follow mode), `CameraInputSampler` (edge field with margin, dwell, focus grace, inside-window
  tracking; middle-drag latched at the press; wheel gated by `OverHud`; key edges; accumulation between Update and
  Render), `CursorConfinePolicy` and `Win32CursorConfiner` (`ClipCursor`, released only while the clip is still ours).
- `TacticalCameraLimits` derives the zoom and leash bounds: `MaxDistance` 21.96 m (every 21:9 view corner inside the
  sun ortho's circle, so any sun azimuth), `LeashMetres` 22.83 m (footprint + leash = the 48 m render radius).
- Integration: the default pose is bit-identical to the fixed `IsometricView`; W/A/S/D follow the camera yaw; fog,
  shadows, lights and props centre on the view target; the war mist's density stays on the player while its slab is
  drawn around the view target; the far plane follows the zoom; Grab cursor while rotating; GLFW hovered is polled each
  frame so a cursor on another monitor stops edge pan; the clip uses `ClientLocation` and is released on pause,
  settings, loading, focus loss, minimise and teardown. Camera settings persist as `camera_*` keys and live on a new
  Options > Camera screen. Capture steps `cam:yaw/zoom/pan/free/follow/reset`.

## Verification

- Full suite: run 1 10683/2, run 2 10721/3, every failure caused by this branch and fixed (pan test above the new
  zoom cap, shipped config missing the camera keys, the clip release added as a counted teardown step); the fixed
  tests then passed in isolation (128/0). No third full run.
- Golden (`run.ps1 -Scene all`, fresh capture-bin): every scene frame passes (0.000000, 0.000000, 0.000003). Each
  scene's `01-play.png` fails on a Build palette and mode label: the baselines (`2ba6d816`) predate master's
  "Start every session in Combat mode" (`64de1c90`), so that frame is stale on master itself, not changed here.
- Captures (`artifacts/captures/cam-*`): default identical framing; yaw 135 rotates world, HUD and nameplates in
  place; W at yaw 135 moves the player screen-up (+0.60, -0.60 m); zoom 10 and max with no shadow or far-plane
  cut-off; a 22 m pan stays under the mist; aim at yaw 135 / zoom 12 `aimcheck` miss 0.000 m HIT. Menu capture:
  Options fits with Key Bindings, Camera and Back.
- Two code reviews (core and integration) found 5 must-fix and 9 should-fix items; all addressed in one fix round.

## Not verified / open

- Edge pan feel, multi-monitor behaviour, confinement in a real fullscreen window and middle-drag feel need a hand
  test: the capture harness never takes focus and drives a scripted pointer, which the camera ignores by design.
- The Camera sub-screen is not in the menu capture flow (`MenuCapture.cs`).
- The server's replication radius for enemies and remote players was not found; with a 22.8 m leash the view can
  reach about 48 m from the player, and if replication is shorter, distant actors pop in at the view edge.
- Golden `01-play` baselines need a re-record on master (unrelated to this branch).
