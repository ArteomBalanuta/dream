# Integrated Host And Client Session Coordination

Purpose: define how the desktop app coordinates integrated local hosting and remote client connection without tangling lifecycle, UI, and gameplay concerns.
Status: approved
Scope: local server handle, validated connection options, coordinator state machine, and handoff into gameplay

## Core Types

### `ILocalServerHandle`

Responsibilities:

- start integrated local server startup
- expose readiness
- expose bound port
- expose startup error if one occurs
- stop and dispose cleanly

### `ClientConnectionOptions`

Responsibilities:

- hold host, port, and player-name input
- validate them
- produce `RemoteClientOptions` for actual connection attempts

### `NetworkSessionCoordinator`

Responsibilities:

- synchronize startup UI with local server readiness
- attempt connection through a connector seam
- expose connection flow state such as idle, connecting, connected, or failed
- hand a successful `RemoteClientSession` back to the desktop shell exactly once

### `IRemoteSessionConnector`

Responsibilities:

- abstract the creation of `RemoteClientSession`
- keep coordinator tests low-harness

## State Model

Recommended states:

- `WaitingForLocalServer`
- `ReadyToConnect`
- `Connecting`
- `Connected`
- `Failed`

## Handoff Rules

- Only `Game` should switch the active scene from startup to gameplay.
- The coordinator should expose a one-time connected-session handoff method.
- `PlayScene` should be constructed only after a successful connection.

## Recommendation

Keep lifecycle orchestration in the coordinator and server-handle seams instead of teaching `Game` or `PlayScene` how to manage transient connection tasks directly. That keeps the startup flow testable and preserves the existing gameplay architecture.
