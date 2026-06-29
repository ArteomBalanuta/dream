# Phase 4 Protocol Contract And Routing

Purpose: record the completed Phase 4 protocol-contract foundation work for the multiplayer roadmap.
Status: completed
Scope: protocol seams, message families, codec/framing, focused routing handlers, and first host/client bootstrap composition

## Summary

Phase 4 introduced the first explicit client/server communication layer for `dream` without claiming a full remote multiplayer stack yet.

Completed work:

- added protocol seam interfaces for transport connections, codec, framing, routing, and role-specific handlers
- added a versioned `TransportEnvelope` plus typed client-to-server and server-to-client message families
- added explicit action payload/result contracts for one-shot gameplay actions:
  - `BuildUseIntent`
  - `CombatUseIntent`
  - `AuthoritativeActionResult`
  - `WorldMutationResult`
  - `CombatResolutionResult`
  - `ClientCorrectionDelta`
  - `RejectedUseDelta`
- added `TransportJsonCodec` with a dedicated `Vector3JsonConverter` so OpenTK vectors serialize cleanly over the wire
- added `LengthPrefixedMessageFramer` for stream-safe message boundaries
- added `ProtocolRouter` with focused server/client handlers instead of scene- or host-level switch blocks
- added `ClientProtocolSession` as the first client-side replicated-message intake seam
- added `DedicatedServerHost`, `ServerBootstrap`, `TcpServerTransport`, and `TcpClientTransport` as first-pass host/bootstrap seams
- updated `Program` so `--server` now routes through `ServerBootstrap`
- updated `PlayScene` so future replicated-state intake can flow through `ClientProtocolSession` instead of growing router logic directly inside the scene

## Verification

Ran:

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter ProtocolFoundationTests /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter ProtocolWiringTests /p:UseSharedCompilation=false`

Results:

- protocol codec, framing, routing, and bootstrap seam tests passed
- verification intentionally stayed low-harness because full end-to-end remote multiplayer hosting is still incomplete

Known limits:

- `DedicatedServerHost.CreateDefault()` is intentionally protocol-first and does not instantiate `WorldSessionRuntime`, because that would drag Bullet native loading into protocol-only tests and bootstrap work before the remote simulation host path is ready
- `ServerBootstrap` still exits honestly instead of starting a long-running accept loop
- remote player spawning, connection pump orchestration, and server-driven runtime ticking over transport remain future phases
