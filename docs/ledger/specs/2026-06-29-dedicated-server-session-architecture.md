# Dedicated Server Session Architecture

Purpose: define the approved architecture direction for adding single-session dedicated-server multiplayer to `dream`.
Status: proposed
Scope: server/client runtime boundaries, server tick ownership, player command flow, replication categories, and first-milestone authority rules

## Goal

Restructure `dream` so one dedicated server process can host one shared world session for many clients, while preserving a practical path to a first working multiplayer proof of concept.

The first milestone must support multiple players in the same world performing the current core actions:

- movement
- camera/look updates
- building
- combat
- shared actor/world observation

## Design Summary

The project should move to a host-split architecture:

- a dedicated server owns authoritative simulation at 64 Hz
- the desktop runtime becomes a client host
- simulation code is extracted into a reusable session runtime
- transport stays replaceable at the boundary
- presentation systems remain client-local unless they affect authority

This is intentionally not a peer-to-peer architecture and not a full engine rewrite.

## Core Principles

### 1. Server authority is the long-term rule

The server owns:

- accepted player transforms
- world mutations
- combat outcomes
- actor lifecycle
- authoritative tick ordering

Clients may predict selected behavior, but the server decides the truth that replication converges on.

### 2. The first PoC should optimize for working shared play, not perfect networking theory

The first transport and replication path should be chosen for practicality. The engine should stay transport-agnostic, but the edge implementation may favor the fastest working proof over an ideal final protocol.

### 3. Simulation must become host-agnostic

Gameplay simulation should not depend on:

- OpenTK window lifecycle
- ImGui
- desktop input objects
- viewmodel rendering
- local HUD ownership

Those remain client concerns.

### 4. Presentation stays local until authority requires more

The following should remain client-local:

- first-person viewmodels
- HUD layout
- floating combat text rendering
- debug overlays
- camera interpolation and smoothing

These systems may react to authoritative state, but they do not need to execute on the server.

## Target Runtime Structure

## Host Layer

### `DedicatedServerHost`

Responsibilities:

- process startup/shutdown
- connection acceptance
- player session registration
- 64 Hz fixed-step loop
- snapshot and event broadcast

It should own no rendering code and no desktop-only input types.

### `ClientGameHost`

This may begin as an evolution of `Game`, or as a thin wrapper around `Game`.

Responsibilities:

- desktop window lifecycle
- input capture
- local command submission
- replicated-state intake
- render and HUD presentation

## Simulation Layer

### `WorldSessionRuntime`

This is the central new boundary.

Responsibilities:

- authoritative map/world state
- authoritative player actor state
- Bullet-backed simulation ownership
- combat/build resolution
- simulation event publication

This runtime should be step-driven by tick input rather than by OpenTK frame events.

### `PlayerSessionRegistry`

Responsibilities:

- track connected players
- map connection/session identity to runtime player actor identity
- support spawn/despawn flow

### `WorldCommandProcessor`

Responsibilities:

- accept per-player command frames
- validate them inside the tick
- apply accepted intents to the authoritative runtime

## Client Presentation Layer

### `ClientScenePresenter`

Responsibilities:

- translate authoritative and interpolated state into renderer input
- distinguish local player presentation from remote player presentation
- own client-only smoothing rules

This keeps `Render`, `WorldRenderer`, `HudRenderer`, and `ViewModelRenderer` client-facing instead of simulation-facing.

## Tick Model

The authoritative server runs at 64 ticks per second.

Each tick should process work in a stable order:

1. gather queued player command frames
2. validate and apply input intent
3. advance movement and physics
4. resolve world interactions
5. resolve combat
6. publish simulation facts
7. build replication output for clients

Client render rate remains independent from server tick rate.

## Player Command Model

The client should stop sending raw device state assumptions and instead send transport-safe command frames.

Recommended command families:

- movement intent
- look/aim intent
- primary use intent
- mode switch intent
- optional jump/freeze/debug intents only if they remain part of supported gameplay

These commands should be sequence-numbered per client so the server and later reconciliation logic can reason about accepted input history.

## Authority Rules

### Server-owned state

- actor transforms as authoritative truth
- world occupancy and placement validity
- health and damage outcomes
- enemy AI state
- accepted interaction mode
- spawn/despawn lifecycle

### Client-owned state

- keyboard/mouse capture
- camera presentation
- first-person animation
- HUD state
- local interpolation buffers

### Hybrid state

- movement prediction
- temporary immediate feedback before confirmation
- correction-aware local motion smoothing

## Replication Model

The replication model should separate categories clearly.

### Snapshots

Use snapshots for current authoritative state such as:

- player transforms
- enemy transforms
- current health
- active world objects

### Deltas or mutation events

Use deltas or mutation events for:

- cube placed
- cube destroyed
- actor spawned
- actor removed
- actor died

### Session metadata

Include:

- server tick number
- actor identity mapping
- last accepted client command sequence

The exact wire format can change later. The engine-facing categories should not.

## Relationship To Existing Repo Types

### `Game`

Becomes purely a client host shell. It should not own simulation truth.

### `PlayScene`

Should shrink from "everything in one local runtime" into a client-facing scene/presenter that consumes authoritative or predicted state and delegates to rendering collaborators.

### `Player`

Should no longer be the one obvious home for both:

- local desktop input handling
- authoritative player simulation
- camera presentation

Instead, the runtime should separate:

- server-side player actor state
- client-side command capture
- client-side camera presentation

### `Map`

Should remain the world façade, but authoritative mutations should flow through validated server tick logic rather than through direct local scene assumptions.

### `PhysicsIntegrator`

Should be treated as an authoritative simulation subsystem, not as a client scene convenience object.

### `SceneEventHub`

Should remain useful for local event-driven reactions, but network replication should be an explicit export layer rather than a side effect of local event publication.

## Non-Goals

This design does not include:

- matchmaking
- persistence or database-backed world storage
- multiple room/session hosting
- anti-cheat hardening beyond server authority basics
- fully generalized transport infrastructure
- rewriting the renderer to be network-aware at every layer

## Success Criteria

- one dedicated server can host one shared session for many clients
- multiple clients can see and interact in the same world
- building and combat flow through server authority
- client render cadence is decoupled from server tick cadence
- client-only presentation systems remain local and do not block the PoC
- the runtime structure is cleaner after the refactor, not more tangled

## Approved Direction

Use a dedicated authoritative server session architecture with:

- one world session per process
- a 64 Hz server tick
- transport-agnostic engine seams
- client-local presentation systems
- selective prediction rather than forced full authority on every visual subsystem from day one
