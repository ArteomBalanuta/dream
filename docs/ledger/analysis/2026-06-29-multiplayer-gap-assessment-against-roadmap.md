# Multiplayer Gap Assessment Against Roadmap

Purpose: audit the current `dream` codebase against the multiplayer roadmap and identify the highest-value missing pieces for a practical local multiplayer client/server product flow.
Status: complete
Scope: roadmap coverage, current multiplayer readiness, protocol and runtime gaps, and implementation priorities for boot-time multiplayer UX

## Summary

The repo is no longer at "multiplayer planning only" status.

Large parts of the original roadmap are already implemented:

- authoritative `WorldSessionRuntime`
- `64 Hz` server tick via `SimulationClock` and `ServerTickRunner`
- transport-neutral snapshot and action DTOs
- protocol envelope, codec, framing, and routing
- dedicated server bootstrap and long-running server service
- desktop TCP client attach path
- multi-session command intake on the server
- remote-player interpolation and rendering
- client correction, rejected action events, and bounded diagnostics

The main remaining gaps are now product-facing and runtime-equivalence gaps, not protocol-foundation gaps.

## Roadmap Coverage Snapshot

### Covered well

- Phase 1 runtime extraction
- Phase 2 player session and command frame seams
- Phase 3 fixed server tick and snapshot export
- Phase 4 protocol contract and host/client transport wiring
- Phase 5 remote-player presentation and interpolation foundation
- large parts of Phase 6 authoritative action routing, correction, and diagnostics

### Partially covered

- many-client support
  - many clients can connect
  - many joined sessions can submit commands
  - but only the first joined remote session owns the full authoritative `Player` path
- shared-world interaction
  - per-session build and first-pass combat authority exist
  - but actor ownership depth is still asymmetric between the primary player and secondary joined players
- client UX
  - remote connect works
  - but only through environment-driven startup config rather than a first-class in-game boot flow

### Not yet covered cleanly

- primary boot-time host/connect screen
- integrated local server lifecycle exposed through normal desktop flow
- retryable connection UX with visible connection state
- equivalent authoritative runtime ownership for all joined player actors
- player-facing multiplayer diagnostics UI

## Highest-Value Missing Pieces

### 1. Primary multiplayer startup flow

Current remote multiplayer requires environment variables before boot.

That is useful for development, but it is not the primary user flow described by the project objective. The next product-facing milestone should be:

- app starts
- local integrated server starts in the background
- boot screen shows host/port fields
- player chooses local or remote target
- connect button attaches the client
- success transitions into `PlayScene`

### 2. Integrated local server lifecycle

The dedicated server stack already exists, but the desktop app does not yet expose it as a first-class local-host path.

The key missing seam is a desktop-owned lifecycle wrapper that:

- starts one authoritative server automatically on app boot
- binds a usable port
- keeps the service alive in the background
- shuts it down cleanly with the game

### 3. Equivalent player actor ownership

The current authoritative host supports many joined sessions, but the runtime still has two actor depths:

- one full authoritative Bullet-backed `Player`
- additional lightweight `RemoteSessionPlayerState` actors

That is good enough for a shared-world PoC, but it remains the largest simulation-side gap against a true multiplayer architecture.

### 4. Protocol efficiency tuning

The current TCP-first stack is acceptable for the working PoC and should remain the current default.

The real protocol issue is not the lack of UDP today. The larger immediate issue is that transport categories are not yet productized around:

- startup/join lifecycle
- steady-state snapshots
- eventual future split between reliable and lower-latency traffic

For now, the clean choice is to keep TCP and continue improving seams rather than adding mixed transports prematurely.

## Recommended Priority Order

1. boot-time startup/connect UX
2. integrated local server lifecycle
3. scene transition and connection-state coordination
4. full actor-equivalence work for all joined players
5. later protocol split or transport specialization if live profiling justifies it

## Recommendation

Treat the next multiplayer phase as a productization pass over an already-real multiplayer foundation.

The best next implementation slice is:

- boot into a multiplayer connection scene
- start a local authoritative server automatically for desktop mode
- connect through the same remote client transport path used for remote servers
- transition into `PlayScene` only after a successful connection

That yields a much more honest client/server architecture without discarding the server/runtime work already completed.
