# Multiplayer Command And Replication Model

Purpose: define the approved command, session, and replication model for the first dedicated-server multiplayer implementation slices.
Status: proposed
Scope: player command frames, player session identity, snapshot categories, world mutation deltas, and client-local presentation boundaries

## Goal

Provide the first concrete multiplayer runtime contracts that allow:

- many clients to connect to one server session
- each client to control one player through command frames
- the server to publish authoritative snapshots and mutation deltas
- the client to render local and remote actors without owning gameplay truth

## Design Summary

The first multiplayer contract model should include:

- explicit player session identity
- per-client command frames
- authoritative actor snapshots
- world mutation deltas
- tick and acknowledgement metadata

The model should stay transport-agnostic and should not embed desktop-only or renderer-only types.

## Player Session Model

Each connected client should map to a stable runtime session identity.

Recommended shapes:

- `PlayerSessionId`
- `ConnectedPlayer`
- `PlayerSessionRegistry`

The registry should track:

- active connections
- authoritative actor identity
- spawn/despawn state
- last accepted command sequence

## Command Model

### `PlayerCommandFrame`

Each client submits one or more command frames containing:

- `PlayerSessionId`
- `Sequence`
- `RequestedMode`
- `MoveIntent`
- `LookIntent`
- `PrimaryUsePressed`

Optional later additions:

- jump intent
- debug/freeze toggles if those remain supported in multiplayer

### `MoveIntent`

The move intent should describe desired directional movement rather than raw key state objects.

Example responsibility:

- forward/backward axis
- strafe axis
- sprint or speed modifier if later added

### `LookIntent`

The look intent should describe authoritative aim change or aim orientation.

For the first pass, either of these is acceptable:

- delta-based yaw/pitch inputs
- absolute yaw/pitch values sampled from the client

The important rule is consistency across client capture and server consumption.

## Server Tick Contract

The server runs at 64 Hz.

For each tick:

1. consume queued command frames
2. resolve the latest usable command for each connected player
3. advance movement and physics
4. resolve world interactions
5. resolve combat
6. produce outbound snapshot and delta data

## Snapshot Model

### `ServerSnapshotEnvelope`

Each outbound server update should include:

- `ServerTick`
- `LastAcceptedCommandSequence` per relevant client
- actor snapshot collection
- mutation delta collection for that send window

### `ActorSnapshot`

The first actor snapshot should support both players and enemies with a shared shape where practical:

- actor id
- actor kind
- position
- velocity or movement basis if needed
- look or facing orientation
- health
- alive/active state

Specialized snapshot types can be added later if the shared shape becomes too weak.

### `WorldSnapshot`

The full world snapshot should primarily be used:

- when a client joins
- when a client requires a coarse resync

For the first PoC, it should include enough map state to reconstruct current placed cubes and important world objects.

## Delta Model

### `WorldMutationDelta`

This should describe accepted authoritative world changes such as:

- cube placed
- cube destroyed
- object spawned
- object removed

### `DamageEventDelta`

This should describe combat outcomes such as:

- actor damaged
- damage amount
- resulting health if useful
- actor died if the hit was fatal

The client may use this for HUD and feedback systems, but the delta itself should stay gameplay-facing.

## Client Presentation Rules

### Client-owned

- input capture
- camera presentation
- first-person viewmodels
- HUD state
- combat text presentation
- interpolation buffers

### Server-owned

- accepted actor transforms
- accepted mode state
- world occupancy
- world mutation validity
- combat outcomes

### Hybrid

- local prediction for feel-sensitive actions
- correction after receiving authoritative state

## Relationship To Existing Repo Types

### `PlayerController`

Moves toward command-frame production.

### `Player`

Stops being the one place where local input, local camera, and authoritative simulation all meet.

### `Map`

Consumes authoritative world mutation results rather than assuming local ownership in multiplayer mode.

### `SceneEventHub`

Remains useful for local reactions, but command and replication contracts are explicit and separate from the local event bus.

## Non-Goals

This contract model does not define:

- the final wire protocol
- matchmaking
- persistence
- multi-room routing
- asset streaming

## Success Criteria

- command frames can represent the current gameplay loop without desktop-only types
- the server can advance many connected players from those commands
- the client can render both local and remote actors from snapshot data
- world mutation and combat replication are explicit rather than implied

## Approved Direction

Use explicit player sessions, command frames, authoritative snapshots, and mutation deltas as the first multiplayer runtime contract layer for `dream`.
