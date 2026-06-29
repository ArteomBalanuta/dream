# Boot Time Connection Flow And Scene Boundaries

Purpose: define how the desktop application should transition from process startup into multiplayer connection and then into gameplay.
Status: complete
Scope: startup scene ownership, UI boundaries, connection lifecycle state, and scene transition responsibilities

## Goal

Make multiplayer connection the primary boot-time user flow without collapsing transport logic into `Game` or `PlayScene`.

## Recommended Boundary Split

### `Game`

Should own:

- top-level desktop app lifecycle
- active scene switching
- shared settings object
- integrated local server lifetime

Should not own:

- socket-level connect logic
- gameplay simulation
- per-frame multiplayer diagnostics logic

### `StartupConnectionScene`

Should own:

- boot-time connection form state
- connection UI rendering
- host/port validation feedback
- connect button handling
- display of local integrated server readiness

Should not own:

- authoritative simulation
- world rendering
- transport encoding/decoding details

### `NetworkSessionCoordinator`

Should own:

- local server readiness status
- validated connection options
- remote client connection task lifecycle
- success/failure connection state transitions

Should not own:

- OpenGL rendering
- gameplay state
- direct scene input polling

### `PlayScene`

Should own:

- gameplay rendering and client presentation
- remote transport message draining after successful connection
- authoritative runtime application and gameplay HUD

Should not own:

- boot-time server selection UI
- local server startup policy

## Connection Flow

1. Desktop process starts
2. Integrated local server begins background startup
3. Startup connection scene renders as the primary scene
4. Player may:
   - use local host defaults
   - enter remote host and port
   - press connect
5. Coordinator attempts the remote client connection
6. On success:
   - `RemoteClientSession` is created
   - `Game` swaps from startup scene into `PlayScene`
7. On failure:
   - startup scene remains active
   - error state is shown
   - player can retry without restarting

## UX Notes

- The startup scene should default to `127.0.0.1` plus the integrated local server port once that port is known.
- Existing environment-variable connection config should remain a developer fallback, but it should no longer be the primary user entry path.
- The menu should keep the cursor visible and normal while active.
- `PlayScene` should only reclaim cursor-grab semantics after gameplay begins.

## Recommendation

Add a dedicated startup connection scene and a focused connection coordinator rather than stretching `PlayScene` into a pre-game network menu. That preserves the current architecture direction where gameplay and startup lifecycle remain separate concerns.
