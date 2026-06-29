# Player Session And Command Model Analysis

Purpose: define how the current single-player runtime should evolve into a multi-client command-driven session model suitable for a dedicated authoritative server.
Status: complete
Scope: player identity, client command capture, authoritative player simulation, and the transition away from direct desktop-input ownership in shared runtime code

## Goal

Replace the current implicit "one local player controlled by local OpenTK input" assumption with an explicit model where:

- many clients can connect
- each client owns a player session identity
- each client sends command frames
- the server advances authoritative player state from those commands

## What Exists Today

The current player stack already has useful decomposition:

- `PlayerController` captures input intent
- `PlayerMotor` applies movement and freeze rules
- `PlayerCameraRig` handles camera presentation
- `PlayerInteraction` handles use/placement edges
- `PlayerCombatant` owns combat-facing health and damage participation

This is a strong starting point because networking does not need to invent player subsystems from scratch. It needs to move ownership boundaries.

## Core Problem

`Player` still acts like a local desktop actor. It mixes:

- authoritative body state
- local input translation
- camera presentation
- interaction timing
- combat participation

That is too much for multiplayer because the server needs only the authoritative parts, while the client needs only the local input/presentation parts plus replicated state.

## Required Split

The player model should separate into three concerns.

### 1. Connection/session identity

New explicit concepts:

- `PlayerSessionId`
- `ConnectedPlayer`
- `PlayerSessionRegistry`

These are not gameplay components. They answer:

- who is connected
- which authoritative actor belongs to that connection
- what connection/session state is still valid

### 2. Command capture and submission

Client-side concepts:

- `PlayerCommandFrame`
- `PlayerMoveIntent`
- `PlayerLookIntent`
- `PrimaryUseIntent`
- `PlayerModeSwitchIntent`

These should be transport-safe, serializable, and independent of OpenTK types at the boundary.

### 3. Authoritative player actor state

Server-side concepts:

- runtime actor transform
- velocity and grounded state
- accepted interaction mode
- combat state
- body/physics ownership

The server should not need `MouseState`, `KeyboardState`, or a camera rig to advance these.

## Recommended Direction For Existing Types

### `PlayerController`

Keep it client-side, but narrow its role.

It should translate desktop input into a command frame, not directly drive authoritative world mutation.

### `PlayerMotor`

This should move closer to a simulation-facing helper.

The server should be able to use the same movement rules, but driven by command intent rather than direct device input.

### `PlayerCameraRig`

This should remain client-local.

The server only needs accepted aim/look state, not a render camera or mouse-look smoothing behavior.

### `PlayerInteraction`

This should be split conceptually into:

- client intent generation
- server validation and execution

Placement should not remain a purely local side effect.

### `PlayerCombatant`

This is already a good candidate for server authority.

Combat truth should remain here or in nearby simulation-owned types, then replicate outward.

## Command Frame Requirements

The first multiplayer command frame should be small and boring.

It should carry:

- client session or actor identity
- command sequence number
- sampled client tick or input frame number if useful
- movement intent
- look intent
- primary use edge
- current requested interaction mode

It should not carry:

- raw device objects
- camera matrices
- presentation-only timing
- renderer-facing state

## Input Edge Rules

The client should own:

- sampling desktop input
- detecting edge-triggered press events
- building command frames
- sending them to the server

The server should own:

- accepting command frames
- validating them against server state
- applying accepted intents in tick order

## Session Registry Rules

The first session registry should stay narrow.

It should answer:

- which players are connected
- which authoritative runtime actor belongs to each session
- which session is awaiting spawn/despawn
- what the last accepted command sequence was for each session

It should not become a giant gameplay profile system yet.

## Likely First File Set

- `dream/PlayerSessionId.cs`
- `dream/ConnectedPlayer.cs`
- `dream/PlayerSessionRegistry.cs`
- `dream/PlayerCommandFrame.cs`
- `dream/PlayerMoveIntent.cs`
- `dream/PlayerLookIntent.cs`
- `dream/PlayerModeSwitchIntent.cs`

The current codebase tends toward flat file organization, so that is a reasonable fit unless a `Networking` or `Multiplayer` folder is introduced deliberately.

## Risks

### Reusing the current `Player` façade unchanged

That would keep the single-player assumptions hidden and make later transport work harder.

### Letting command DTOs depend on OpenTK

That would tie shared runtime contracts to the desktop shell.

### Treating the camera as authoritative state

The camera is local presentation. Aim state may be authoritative, but the camera rig itself is not.

### Encoding too many features into the first command frame

The first frame should only support what the current gameplay loop truly needs.

## Recommendation

The first multiplayer implementation slice should establish session identity and command frames before transport details spread through the codebase.

That is the cleanest way to convert the existing player decomposition into a server-authoritative multi-client runtime.
