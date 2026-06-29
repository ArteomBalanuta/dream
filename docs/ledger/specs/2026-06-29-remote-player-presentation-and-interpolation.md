# Remote Player Presentation And Interpolation

Purpose: define the approved client-side architecture for rendering remote players from authoritative snapshots without collapsing local first-person presentation and remote world-space presentation into the same system.
Status: proposed
Scope: remote-player visual state, interpolation ownership, client presenter boundaries, world-space remote rendering, and local-only first-person rules

## Goal

Render remote players smoothly on the client while preserving the rule that:

- the server owns gameplay truth
- the client owns presentation
- the local first-person stack stays local-only

## Design Summary

The client should introduce a dedicated remote-player presentation seam that:

- consumes authoritative snapshots and deltas
- stores recent remote-player samples
- interpolates remote player poses at render time
- exposes world-space render state to a dedicated remote-player renderer

This should not reuse the local camera/viewmodel path.

## Core Principles

### 1. Remote players are world-space actors

Remote players should be rendered in the world, not through first-person presentation systems.

That means they do not own:

- local camera state
- local HUD state
- first-person viewmodels
- local floating combat text ownership

### 2. Interpolation is client-local

The server sends authoritative snapshots.

The client chooses how to:

- buffer them
- interpolate between them
- present them at render time

This behavior should not leak back into server tick scheduling.

### 3. `PlayScene` should stay a client orchestrator, not a remote-player bag of state

Remote-player intake and interpolation should live behind a dedicated presenter seam rather than being hand-managed in scene update/render methods.

## Recommended Types

### `ClientScenePresenter`

Responsibilities:

- receive snapshot and delta input
- update remote-player interpolation buffers
- expose render-ready remote-player state
- keep local player versus remote-player presentation boundaries explicit

### `RemotePlayerPresentationState`

Responsibilities:

- current client-side remote actor visual state
- last applied authoritative snapshot tick
- current interpolated pose
- minimal health/lifecycle flags needed by rendering

### `RemotePlayerSnapshotSample`

Responsibilities:

- immutable buffered remote sample
- position/orientation data
- source snapshot tick/time

### `RemotePlayerInterpolationBuffer`

Responsibilities:

- store recent ordered samples
- choose interpolation window
- return one `InterpolatedActorPose` for render consumption

### `RemotePlayerRenderer`

Responsibilities:

- draw remote players in world space
- stay separate from `WorldRenderer` cube work
- stay separate from `ViewModelRenderer`

## Render Ownership Rules

### Local player

The local player keeps:

- camera rig
- first-person viewmodel path
- HUD and ImGui ownership
- local combat text and client-side feedback rendering

### Remote players

Remote players get:

- snapshot-driven visual state
- interpolation buffers
- world-space draw path

### Shared render rule

Both local and remote actors may derive from the same authoritative snapshot family, but their presentation paths should remain different.

## Snapshot Timing Rule

The server sends data at fixed tick cadence.

The client should render remote players from a slightly delayed interpolation target rather than trying to display the very latest sample immediately.

This buys smoother movement and fewer visible snaps when client render cadence differs from 64 Hz.

## Relationship To Existing Repo Types

### `PlayScene`

Should hand replicated snapshot input to `ClientScenePresenter` and consume its render output.

### `Render`

Should expose a dedicated collaborator for remote-player drawing rather than forcing that work into HUD or viewmodel systems.

### `WorldRenderer`

Should remain focused on world geometry. Remote-player rendering may share low-level drawing utilities later, but it should not be absorbed into cube batching logic.

### `PlayerCameraRig`

Should remain local-only.

Remote players must not reuse local camera logic.

## Non-Goals

This spec does not define:

- final character art or animation assets
- voice/chat presentation
- spectator cameras
- server-side interpolation rules

## Success Criteria

- remote players render in world space
- local player camera/HUD/viewmodel ownership stays intact
- snapshot interpolation remains client-only
- `PlayScene` becomes cleaner instead of owning more remote-player details
- remote-player visuals can be driven by loopback or in-process snapshot feeds before final transport is complete

## Approved Direction

Use a dedicated client presenter plus interpolation buffer path for remote players, and keep first-person local presentation separate from replicated world-space actor presentation.
