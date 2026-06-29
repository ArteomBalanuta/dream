# Primary Multiplayer Startup Flow

Purpose: define the new primary desktop startup experience for multiplayer in `dream`.
Status: approved
Scope: startup scene UX, local integrated server defaults, connection-state behavior, and transition into gameplay

## Requirements

### Boot behavior

- Starting the desktop app should no longer enter gameplay immediately.
- The app should start on a dedicated multiplayer startup screen.
- A local integrated server should begin starting automatically in the background during desktop boot.

### Startup screen

The startup screen must:

- show a host field
- show a port field
- show a player-name field
- show the local integrated server status
- provide a `Use Local Server` action
- provide a `Connect` action
- show connection errors without forcing an app restart

### Connection behavior

- `Use Local Server` should populate the host and port fields from the integrated local server once ready.
- `Connect` should validate the current host/port/player-name form state.
- On successful connection, the game should transition into `PlayScene`.
- On failed connection, the startup scene should remain active and display a retryable error.

### Fallback behavior

- The current environment-variable connection path should remain available for development workflows.
- The environment-variable path should no longer be the primary intended user flow.

## Architectural Constraints

- The startup screen should not own gameplay or simulation logic.
- The startup screen should not encode or decode protocol messages directly.
- The integrated local server should use the same transport/client connection path as any remote host.
- `PlayScene` should remain the gameplay scene and should not become the boot-time connection menu.

## Non-Goals

- replacing the current TCP transport
- adding lobby chat or matchmaking
- solving full multi-actor authoritative equivalence for every joined player

## Acceptance Criteria

- desktop boot opens the startup connection screen
- local integrated server startup status is visible
- local server host/port can be selected without environment variables
- remote host/port can be entered manually
- successful connection transitions into gameplay
- failed connection leaves the startup screen alive with visible feedback
