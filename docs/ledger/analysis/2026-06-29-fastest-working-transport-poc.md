# Fastest Working Transport PoC

Purpose: choose the best-fit first transport shape for the dedicated-server multiplayer proof of concept without overcommitting the engine to a premature networking stack.
Status: complete
Scope: server bootstrap edge, client connection edge, transport abstraction boundaries, and the minimum message framing needed for the first shared-world multiplayer slice

## Goal

Pick a transport direction that gets `dream` to a working local multiplayer proof of concept quickly while preserving clean engine seams for later improvement.

The choice here is not the final network architecture for the project. It is the fastest credible path to:

- one authoritative server
- many clients
- one shared world session
- command submission and snapshot intake

## Recommendation

For the first PoC, use a simple TCP-style client/server edge behind narrow transport interfaces.

Recommended first files:

- `dream/IServerTransport.cs`
- `dream/TcpServerTransport.cs`
- `dream/IClientTransport.cs`
- `dream/TcpClientTransport.cs`
- `dream/TransportEnvelope.cs`
- `dream/TransportJsonCodec.cs`

## Why TCP Fits This Repo First

### 1. The current risk is runtime structure, not packet efficiency

The largest multiplayer risks in this repo are:

- extracting simulation from the current local scene flow
- supporting many player sessions
- separating commands from desktop input
- separating replication from local presentation

Those are more urgent than transport optimization.

### 2. A simple stream transport reduces PoC friction

A TCP-style path gives the first server host:

- easier connection lifecycle management
- simpler local loopback testing
- fewer moving parts while command and snapshot DTOs are still settling
- easier logging of join/command/snapshot flow during early debugging

### 3. The engine can remain transport-agnostic

The key architectural rule is not "TCP forever."

The key rule is:

- gameplay/runtime code depends on command and snapshot contracts
- transport implementations depend on framing and serialization
- the two meet only through small transport interfaces

If this rule holds, later transport replacement stays practical.

## What The First Transport Must Carry

The initial message family should be intentionally small:

- client join/connect
- client disconnect
- `PlayerCommandFrame`
- `ServerSnapshotEnvelope`
- optional server action/result envelope for mutation and correction flow

No asset streaming, matchmaking, or account/session management is needed here.

## Bootstrap Direction

The first dedicated server path should start through a headless bootstrap split:

- `Program.cs` detects `--server`
- `ServerBootstrap` creates `DedicatedServerHost`
- `DedicatedServerHost` owns `WorldSessionRuntime`, `PlayerSessionRegistry`, and `ServerTickRunner`

The client path should stay separate:

- `Program.cs` default path starts desktop runtime
- later client networking hooks feed command frames out and snapshots back in

## Serialization Direction

For the first PoC, use a simple explicit codec such as `TransportJsonCodec`.

This is appropriate because:

- DTOs are still evolving
- readability helps early multiplayer debugging
- correctness and iteration speed matter more than payload compactness

Later phases can optimize framing or payload shape if real measurements justify it.

## Boundaries To Preserve

### Transport layer should not know gameplay rules

It should only know:

- how to send and receive envelopes
- how to open and close connections
- how to surface inbound messages to the host

### Simulation layer should not know socket details

`WorldSessionRuntime` and nearby runtime types should not care whether commands came over:

- loopback TCP
- a future UDP-style transport
- in-process test harnesses

### Presentation layer should not become the transport owner of truth

The client render and HUD stack should consume replicated outcomes, not decide them.

## Repo-Specific Hazards

### `PlayScene` still owns too much local construction

If the client transport gets bolted directly into the current `PlayScene` ownership model, the server/client split will blur immediately.

### `PhysicsIntegrator` still assumes one local player body

This makes the host/runtime refactor more urgent than transport complexity.

### `Player.Update()` still mixes local camera and authoritative movement assumptions

A transport path alone does not fix that.

### Presentation-heavy runtime asset loading must stay out of the server

The headless server path should not accidentally initialize:

- OpenTK windowing
- ImGui
- shader/model/font paths

## Non-Goals

This PoC transport decision does not attempt to solve:

- packet loss optimization
- bandwidth minimization
- anti-cheat hardening
- cross-region hosting
- multi-room routing

## Recommendation

Use a TCP-style transport edge for the first multiplayer proof of concept, but keep it behind narrow interfaces so the runtime and replication contracts stay transport-agnostic.

That is the best-fit "working first" choice for the current `dream` codebase.
