# Multiplayer Authoritative Server Roadmap Analysis

Purpose: define the recommended roadmap for evolving `dream` from a local desktop sandbox into a single-session dedicated-server multiplayer runtime.
Status: complete
Scope: authoritative server model, client/server ownership, phased refactor strategy, and PoC-oriented replication priorities

## Goal

Support many clients connecting to one authoritative dedicated server that owns the shared world simulation, while keeping the first proof of concept practical for the current codebase.

The first multiplayer milestone should allow multiple remote players to:

- move
- look
- build
- fight
- coexist in one shared world

Settings, menus, matchmaking, persistence, and multi-room hosting are intentionally out of scope.

## Chosen Direction

The best-fit architecture for this repo is:

- one shared world session per server process
- a dedicated authoritative server tick at 64 Hz
- transport-agnostic engine boundaries
- the fastest practical PoC transport at the edge
- selective client-local prediction and presentation systems

This is not a lockstep design and not a peer-to-peer design.

## Why A Hybrid Vertical Slice Is The Right Roadmap

The repo is already partway through a healthy structural cleanup. A direct jump from the current desktop scene into a fully generalized network engine would overbuild the project and likely stall the first multiplayer milestone.

The right roadmap is a hybrid vertical slice:

- define server authority rules early
- extract the minimum simulation seams needed for a headless runtime
- preserve client-only presentation systems where that reduces risk
- prove the architecture with a working shared-world PoC before broadening scope

## Recommended Ledger Outputs

The roadmap should be recorded through this document set:

- analysis:
  - `docs/ledger/analysis/2026-06-29-client-server-refactor-seams.md`
  - `docs/ledger/analysis/2026-06-29-multiplayer-authoritative-server-roadmap.md`
- spec:
  - `docs/ledger/specs/2026-06-29-dedicated-server-session-architecture.md`
- plan:
  - `docs/ledger/plans/2026-06-29-multiplayer-foundation-roadmap.md`
- decision:
  - `docs/ledger/decisions/2026-06-29-authoritative-server-tick-boundary.md`
- work log:
  - `docs/ledger/work-log/2026-06-29-multiplayer-roadmap-planning.md`

## Recommended Architecture Outline

### Dedicated server host

Add a future host such as `DedicatedServerHost` that owns:

- server process lifetime
- fixed 64 Hz tick scheduling
- connection acceptance
- player session registry
- authoritative simulation execution
- replication broadcast

It should not depend on OpenTK, ImGui, or viewmodel/HUD code.

### Client host

The current desktop runtime should evolve toward a client host such as `ClientGameHost` or a thinner `Game` + `ClientScenePresenter` split that owns:

- desktop window lifecycle
- rendering
- input capture
- command submission
- replication intake
- local smoothing, interpolation, and optional prediction

### Shared simulation runtime

Extract a host-agnostic simulation root such as `WorldSessionRuntime`.

It should own:

- authoritative player entities
- map/world state
- Bullet physics ownership
- combat resolution
- build/destruction resolution
- server-side event publication

This runtime should be stepped by the dedicated server and described in fixed ticks rather than in terms of desktop frame callbacks.

## Authority Matrix

### Server-owned

- world grid occupancy
- cube placement validity
- cube destruction validity
- rigid-body authority
- player transforms as accepted truth
- combat resolution
- health and life state
- interaction-mode truth
- actor spawning and despawning
- replicated snapshot timestamps and sequencing

### Client-owned

- raw keyboard and mouse capture
- local camera presentation
- HUD layout and ImGui behavior
- first-person viewmodel animation
- floating combat text rendering
- interpolation buffers
- optional local-only debug visualization

### Hybrid or predicted

- movement prediction
- camera smoothing from authoritative transforms
- local fire/use responsiveness before correction
- temporary cosmetic feedback before authoritative confirmation

The client may predict for feel, but the server is the source of truth.

## Replication Priorities For The PoC

The first PoC should replicate categories in this order:

### 1. Session and identity

- player connected
- player assigned runtime identity
- player spawned in the world
- player disconnected

### 2. Player transform state

- position
- velocity or movement basis
- look yaw/pitch or equivalent aim state
- grounded/frozen/local mode flags if needed for remote presentation

### 3. World mutations

- cube placed
- cube destroyed
- floor/world reset if that remains a supported runtime action

### 4. Combat state

- damage applied
- health changed
- actor died

### 5. Actor lifecycle

- enemy spawned
- enemy moved
- enemy damaged
- enemy removed

### 6. Snapshot metadata

- server tick number
- last processed client command sequence
- coarse latency or correction markers if needed

## Recommended Phases

### Phase 1: headless-ready simulation extraction

Scope:

- separate simulation ownership from desktop shell ownership
- identify what in `PlayScene` must move into a host-agnostic runtime
- stop tying authoritative logic to render/HUD presence

Dependencies:

- current `Game` vs `PlayScene` split
- current player/world/physics helper decomposition

Risks:

- over-extracting too early
- keeping hidden client-only assumptions inside the runtime

Likely touched areas:

- `dream/PlayScene.cs`
- `dream/Game.cs`
- `dream/Player.cs`
- `dream/Map.cs`
- `dream/PhysicsIntegrator.cs`

### Phase 2: player session and command model

Scope:

- introduce connected-player identity and multi-player ownership
- replace direct local-only player input assumptions with command frames
- define per-tick command intake

Dependencies:

- simulation root extraction

Risks:

- leaving one-player shortcuts in world/combat code
- leaking OpenTK input types into shared runtime contracts

Likely touched areas:

- `dream/PlayerController.cs`
- `dream/Player.cs`
- new command/session types

### Phase 3: server tick and deterministic-enough update order

Scope:

- introduce a 64 Hz authoritative tick loop
- define fixed simulation ordering for input, physics, world mutation, combat, and events
- separate server tick time from client render time

Dependencies:

- command/session model

Risks:

- allowing frame-time assumptions to survive inside the runtime
- mixing client smoothing logic into server time flow

Likely touched areas:

- `dream/PlayScene.cs` or extracted runtime
- `dream/PhysicsIntegrator.cs`
- new tick scheduling types

### Phase 4: replication model foundation

Scope:

- define snapshots, deltas, and event envelopes
- choose the minimum replication categories for the PoC
- ensure replication payloads are not raw scene objects

Dependencies:

- server-owned player/world authority

Risks:

- serializing engine implementation types directly
- coupling transport framing to gameplay types

Likely touched areas:

- new replication DTOs
- world/player/combat export helpers

### Phase 5: server host and client transport edge

Scope:

- add a dedicated server host process path
- add the easiest working transport for the first PoC
- keep the transport behind small interfaces

Dependencies:

- tick loop
- replication payloads

Risks:

- transport leaking into gameplay flow
- building too much reliability infrastructure before proving the PoC

Likely touched areas:

- new host/bootstrap files
- connection/session management

### Phase 6: remote player presentation

Scope:

- render many remote players
- distinguish local-first-person presentation from remote-world presentation
- keep remote presentation client-only

Dependencies:

- replicated actor snapshots

Risks:

- reusing the local `PlayerCameraRig` model for remote actors
- confusing local viewmodel state with remote world models

Likely touched areas:

- renderer-facing actor presentation
- future remote player visual state types

### Phase 7: authoritative world interaction

Scope:

- run build and combat interactions through server validation
- replicate accepted mutations and corrections
- ensure remote clients converge after conflicts

Dependencies:

- command flow
- replication model

Risks:

- local mutation happening before authority checks
- desync between map, physics, and combat state

Likely touched areas:

- `dream/PlayerInteraction.cs`
- `dream/Map.cs`
- combat/world mutation systems

### Phase 8: correction, smoothing, and observability

Scope:

- add minimal prediction correction flow
- log tick, command, and snapshot sequencing
- support debugging desyncs

Dependencies:

- working multiplayer PoC

Risks:

- chasing visual polish before correctness
- lacking instrumentation for state divergence

Likely touched areas:

- client reconciliation helpers
- event trace or networking diagnostics

## Top Repo-Specific Hazards

1. `PlayScene` still assumes one local player, one local map, one local physics world, and one local render stack.
2. `Player` still mixes simulation ownership with camera-facing behavior.
3. `PlayerController` currently targets desktop device state, not transport-safe command frames.
4. `Map` and `WorldPhysicsBinder` still assume direct local mutation rather than server command validation.
5. `PhysicsIntegrator` still lives as a scene-owned helper instead of an authoritative runtime subsystem.
6. `PhysicsSyncSystem` currently flows from Bullet into local cube visuals, not into replication/export shapes.
7. `Render` and its collaborators currently assume the authoritative world is in-process and immediately available.
8. `SceneEventHub` events are useful for local reactions, but should not automatically become network messages.
9. Current enemy/combat systems likely assume one local player as the primary target and interaction initiator.
10. The current project still carries prototype-oriented absolute assumptions, so multiplayer work must keep tightening runtime boundaries instead of adding features sideways.

## Recommendation

Do not start with matchmaking, persistence, or generalized netcode infrastructure.

Start with:

1. extract a host-agnostic authoritative simulation core
2. introduce multi-player session and command ownership
3. establish the 64 Hz server tick
4. replicate the smallest useful shared-world state
5. add correction and smoothing only after the PoC works end to end

That is the shortest path from the current repo to a credible dedicated-server multiplayer foundation.
