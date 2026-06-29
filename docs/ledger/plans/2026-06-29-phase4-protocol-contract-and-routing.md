# Phase 4 Protocol Contract And Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish the clean protocol contract and routing layer for client/server communication before the transport PoC grows into ad hoc message handling.

**Architecture:** Introduce explicit protocol interfaces, a versioned transport envelope, typed client/server message families, and focused protocol routers/handlers so transport, protocol, and runtime stay decoupled.

**Tech Stack:** C# / .NET 10, JSON PoC serialization, TCP-style transport edge, authoritative runtime and snapshot DTOs from earlier multiplayer phases

---

### Task 1: Add protocol seam interfaces

**Files:**
- Create: `dream/ITransportConnection.cs`
- Create: `dream/IProtocolCodec.cs`
- Create: `dream/IMessageFramer.cs`
- Create: `dream/IProtocolRouter.cs`
- Create: `dream/IClientMessageHandler.cs`
- Create: `dream/IServerMessageHandler.cs`
- Modify: `docs/architecture.md`

- [ ] Add interfaces only for behavior seams that may vary by implementation
- [ ] Keep the interfaces focused on transport, framing, codec, and routing responsibilities
- [ ] Avoid adding low-value interfaces for passive DTO records
- [ ] Document the layer split so future protocol work follows the same boundaries

### Task 2: Define the versioned envelope and core message families

**Files:**
- Create: `dream/TransportEnvelope.cs`
- Create: `dream/IProtocolMessage.cs`
- Create: `dream/IClientToServerMessage.cs`
- Create: `dream/IServerToClientMessage.cs`
- Create: `dream/ClientHelloMessage.cs`
- Create: `dream/ClientJoinRequestMessage.cs`
- Create: `dream/PlayerCommandMessage.cs`
- Create: `dream/PlayerActionMessage.cs`
- Create: `dream/ServerHelloMessage.cs`
- Create: `dream/JoinAcceptedMessage.cs`
- Create: `dream/JoinRejectedMessage.cs`
- Create: `dream/ServerSnapshotMessage.cs`
- Create: `dream/ServerActionMessage.cs`

- [ ] Define a single protocol envelope with version, kind, sequence, and payload metadata
- [ ] Keep message names direction-specific so the protocol remains readable
- [ ] Cover only the first PoC message families needed by the multiplayer roadmap
- [ ] Avoid creating one giant nullable message type

### Task 3: Define action payload and result contracts

**Files:**
- Create: `dream/IPlayerActionPayload.cs`
- Create: `dream/IActionResultPayload.cs`
- Modify: `dream/BuildUseIntent.cs`
- Modify: `dream/CombatUseIntent.cs`
- Modify: `dream/AuthoritativeActionResult.cs`
- Modify: `dream/WorldMutationResult.cs`
- Modify: `dream/CombatResolutionResult.cs`
- Modify: `dream/ClientCorrectionDelta.cs`
- Modify: `dream/RejectedUseDelta.cs`

- [ ] Use one clean action pattern for client-submitted actions and server-returned action results
- [ ] Keep action payloads explicit and transport-safe
- [ ] Keep action results explicit so clients can confirm or correct local assumptions clearly
- [ ] Avoid leaking envelope concerns into the action DTOs themselves

### Task 4: Add codec, framing, and routing implementations

**Files:**
- Create: `dream/TransportJsonCodec.cs`
- Create: `dream/LengthPrefixedMessageFramer.cs`
- Create: `dream/ProtocolRouter.cs`
- Create: `dream/JoinRequestHandler.cs`
- Create: `dream/PlayerCommandHandler.cs`
- Create: `dream/PlayerActionHandler.cs`
- Create: `dream/ServerSnapshotMessageHandler.cs`
- Create: `dream/ServerActionMessageHandler.cs`

- [ ] Keep JSON serialization in the codec layer and stream-safety in the framer layer
- [ ] Route messages through focused handlers instead of growing one large host or scene switch block
- [ ] Keep handler responsibilities narrow and role-specific
- [ ] Keep runtime invocation behind routing adapters rather than direct socket code

### Task 5: Wire the protocol layer into host/client paths and verify

**Files:**
- Modify: `dream/DedicatedServerHost.cs`
- Modify: `dream/ServerBootstrap.cs`
- Modify: `dream/TcpServerTransport.cs`
- Modify: `dream/TcpClientTransport.cs`
- Modify: `dream/PlayScene.cs`
- Create: `docs/ledger/work-log/2026-06-29-phase4-protocol-contract-and-routing.md`

- [ ] Wire the server host through the protocol router instead of ad hoc message parsing
- [ ] Wire the client path through protocol message handling without turning `PlayScene` into the router
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo` and record protocol verification limits honestly

## Self-Review Notes

- Spec coverage: this phase covers seam interfaces, envelope/message contracts, action payload/result patterns, codec/framing/routing, and host wiring
- Placeholder scan: the plan names concrete files and bounded responsibilities throughout
- Type consistency: `IProtocolCodec`, `IMessageFramer`, `TransportEnvelope`, `IProtocolRouter`, and the role-specific message families remain stable throughout
