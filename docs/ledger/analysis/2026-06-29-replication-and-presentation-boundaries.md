# Replication And Presentation Boundaries Analysis

Purpose: identify the replication categories and client-local presentation boundaries needed for the first multiplayer proof of concept.
Status: complete
Scope: snapshots, mutation events, remote-player presentation, client-local visuals, and the line between authoritative state and render-facing state

## Goal

Define what the server must replicate, what the client may derive locally, and how the current rendering stack should consume shared-world state without becoming the owner of multiplayer truth.

## Current Repo Shape

The rendering and HUD path is already fairly decomposed:

- `Render`
- `WorldRenderer`
- `EnemyRenderer`
- `DebugRenderer`
- `ViewModelRenderer`
- `HudRenderer`
- `CombatFeedbackSystem`

This is useful because multiplayer should not push replication logic into all of these types equally. Most of them should remain presentation-only consumers.

## Key Boundary Rule

Replication should carry gameplay truth, not client rendering internals.

That means the server replicates:

- actor/world state
- accepted mutations
- combat outcomes
- snapshot/tick metadata

The client derives:

- interpolation
- HUD effects
- first-person animation state
- floating combat text rendering
- camera-local polish

## Replication Categories

### 1. Session bootstrap

Clients need enough information to join the session:

- local actor identity
- current server tick
- initial world snapshot
- other visible actors

This should be treated differently from steady-state per-tick updates.

### 2. Actor snapshots

The first actor snapshot layer should include:

- actor id
- actor kind
- position
- orientation or aim basis
- health
- lifecycle flags if needed

This covers:

- local player correction
- remote player rendering
- enemy rendering

### 3. World mutation deltas

The world does not need full resends on every tick for the current project scale.

The first PoC can rely on:

- initial full world snapshot on join
- deltas for cube placed
- deltas for cube destroyed
- later full resync only as a correction tool

### 4. Combat result events

For the current gameplay loop, the client needs to learn:

- damage happened
- health changed
- actor died

The client does not need the full server-side combat reasoning chain.

### 5. Tick and acknowledgement metadata

Clients need metadata for:

- snapshot ordering
- correction ordering
- which command frame sequence the server has processed

Without this, even a basic reconciliation path becomes difficult.

## Recommended Presentation Split

### Remote players

Remote players should be represented through a dedicated presentation state such as:

- `RemotePlayerPresentationState`
- `RemoteActorVisualState`

These types should be fed from replicated snapshots and interpolation buffers, not from local `PlayerCameraRig` ownership.

### Local player

The local player should continue to own:

- first-person camera
- viewmodel state
- HUD state

But those should sit on top of authoritative or predicted state, not define it.

### Enemies

Enemy presentation should follow the same broad rule as remote players:

- authoritative state replicated from the server
- local interpolation and render adaptation on the client

## Current Types That Should Stay Client-Only

- `HudRenderer`
- `ViewModelRenderer`
- `PlayerCameraRig`
- `CombatFeedbackSystem`
- `FloatingCombatTextSystem`
- most of `Render`

These may respond to replicated outcomes, but the server should not wait for them or depend on them.

## Current Types That Need Replication Adapters

- `Map`
- `PlayerCombatant`
- `EnemyRegistry`
- `EnemyActor`
- `EnemyCombatant`
- `PhysicsSyncSystem`

These types either hold authoritative state directly or are currently too close to the in-process truth and will need export/import seams.

## Recommended First DTO Families

- `ActorSnapshot`
- `PlayerSnapshot`
- `EnemySnapshot`
- `WorldSnapshot`
- `WorldMutationDelta`
- `DamageEventDelta`
- `ServerSnapshotEnvelope`

The exact class names can change, but the family split matters.

## Risks

### Sending renderer-facing state over the wire

That would couple the network layer to client implementation details and make server evolution harder.

### Treating local events as network payloads by default

`SceneEventHub` is useful for local reactions, but replication should be an explicit export layer.

### Making remote players reuse the local first-person stack

Remote actors should use world-space presentation, not the local camera/viewmodel path.

### Over-resending world state

For this repo, cube placement/destruction deltas are a better first PoC than constant full-world replication.

## Recommendation

The multiplayer PoC should replicate only what the shared simulation needs and let the client continue owning presentation-heavy systems.

That keeps the networking path aligned with the repo’s current rendering decomposition and avoids turning the render layer into the multiplayer authority model.
