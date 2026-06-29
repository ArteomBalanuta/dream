# Client Server Protocol Contract

Purpose: define the approved protocol contract for client/server communication in the first multiplayer implementation slices of `dream`.
Status: proposed
Scope: message families, protocol envelope metadata, interface seams, routing rules, action payload patterns, and boundaries between transport, protocol, and runtime

## Goal

Define a clean protocol contract that allows:

- a dedicated server to accept clients
- clients to submit player commands and actions
- the server to return authoritative snapshots and action results
- transport and gameplay logic to evolve independently

## Design Summary

The protocol should be structured around:

- narrow transport interfaces
- explicit framing and codec interfaces
- one versioned protocol envelope
- typed client-to-server and server-to-client message families
- focused protocol routers/handlers
- concrete action payload and action result DTOs

This keeps the multiplayer path clean without overengineering the first PoC.

## Protocol Layers

### Transport layer

Interfaces:

- `IClientTransport`
- `IServerTransport`
- `ITransportConnection`

Responsibilities:

- connect/listen
- send and receive framed payloads
- close connections
- surface connection lifecycle changes

### Codec and framing layer

Interfaces:

- `IProtocolCodec`
- `IMessageFramer`

Responsibilities:

- serialize and deserialize envelopes
- frame messages safely on a stream transport
- reject malformed or incompatible protocol data

### Protocol layer

Interfaces:

- `IProtocolMessage`
- `IClientToServerMessage`
- `IServerToClientMessage`

Responsibilities:

- define legal message families
- keep payloads serializable and role-specific
- carry protocol metadata independently from runtime classes

### Runtime adapter layer

Interfaces:

- `IProtocolRouter`
- `IClientMessageHandler`
- `IServerMessageHandler`

Responsibilities:

- map inbound protocol messages to runtime actions
- map runtime snapshots/results to outbound messages
- keep `WorldSessionRuntime` free of transport and envelope knowledge

## Envelope Contract

The first protocol envelope should include:

- `ProtocolVersion`
- `MessageKind`
- `Sequence`
- optional `SessionId`
- one typed payload

Recommended first type:

- `TransportEnvelope`

The envelope exists to support routing, versioning, and framing concerns. It should not become a bag of gameplay state.

## Message Families

### Client to server

- `ClientHelloMessage`
- `ClientJoinRequestMessage`
- `PlayerCommandMessage`
- `PlayerActionMessage`
- `ClientDisconnectMessage`
- optional `ClientPingMessage`

### Server to client

- `ServerHelloMessage`
- `JoinAcceptedMessage`
- `JoinRejectedMessage`
- `ServerSnapshotMessage`
- `ServerActionMessage`
- `ServerDisconnectMessage`
- optional `ServerPingReplyMessage`

## Action Pattern

Use one explicit action pattern instead of many loose booleans:

- `PlayerActionMessage` carries one action payload
- `IPlayerActionPayload` is the marker/interface seam for action payload families
- concrete payloads include:
  - `BuildUseIntent`
  - `CombatUseIntent`

For authoritative outcomes:

- `ServerActionMessage` carries one action result payload
- `IActionResultPayload` is the marker/interface seam for result families
- concrete payloads include:
  - `AuthoritativeActionResult`
  - `WorldMutationResult`
  - `CombatResolutionResult`
  - `ClientCorrectionDelta`
  - `RejectedUseDelta`

## Routing Rules

Do not make hosts or scenes own giant protocol `switch` statements forever.

Recommended pattern:

- `IProtocolRouter` dispatches by `MessageKind`
- dedicated handlers process each family

Examples:

- `JoinRequestHandler`
- `PlayerCommandHandler`
- `PlayerActionHandler`
- `SnapshotMessageHandler`
- `ServerActionMessageHandler`

The first implementation can start small, but it should preserve this boundary.

## Serialization Rules

For the first PoC:

- use explicit JSON serialization through `TransportJsonCodec`
- include stable property names
- reject unknown or incompatible envelope versions at the codec/router boundary

Human-readable payloads are acceptable for the PoC because debuggability matters more than compactness at this stage.

## Runtime Boundary Rules

### `WorldSessionRuntime`

Must not depend on:

- socket types
- transport connection objects
- protocol envelope types
- JSON codec details

### `PlayScene`

May consume replicated snapshots and action results, but should not become the permanent protocol router.

### `SceneEventHub`

May publish local reactions after protocol messages are applied, but is not the wire protocol itself.

## Clean-Code Rules

### Prefer interfaces for behavior seams

Use interfaces when the implementation may vary:

- transport
- codec
- framer
- router
- message handler

### Prefer concrete records for DTOs

Use concrete record/DTO types for payloads such as:

- join data
- snapshots
- mutation deltas
- action intents
- action results

### Keep naming role-specific

Names should communicate direction clearly:

- `ClientJoinRequestMessage`
- `ServerSnapshotMessage`
- `PlayerActionMessage`

Avoid ambiguous names such as `Message` or `Update`.

## Protocol Version Rule

Every client/server session should validate protocol version compatibility at handshake time.

If versions are incompatible:

- reject the join cleanly
- emit a readable reason

This avoids silent corruption or mismatched payload assumptions.

## Non-Goals

This contract does not define:

- final binary protocol optimization
- compression
- encryption
- production anti-cheat systems
- account identity
- matchmaking

## Success Criteria

- transport can change without rewriting `WorldSessionRuntime`
- message families stay understandable and role-specific
- action payloads and results remain explicit
- protocol routing stays outside scene/gameplay god objects
- the first multiplayer PoC remains debuggable

## Approved Direction

Use an interface-driven protocol contract with explicit envelopes, typed message families, focused routers/handlers, and concrete action payload/result DTOs.
