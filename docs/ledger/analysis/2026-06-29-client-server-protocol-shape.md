# Client Server Protocol Shape

Purpose: define the recommended protocol structure for client/server communication in `dream` so the first multiplayer implementation stays clean, testable, and transport-agnostic.
Status: complete
Scope: message families, protocol-layer interfaces, routing patterns, action contracts, framing responsibilities, and clean-code boundaries between transport and gameplay

## Goal

Specify a protocol shape that is:

- simple enough for the first multiplayer PoC
- explicit enough to avoid protocol drift
- interface-driven so transport and gameplay stay decoupled
- clean enough to evolve without rewriting the runtime

## Current State

The ledger already defines:

- a TCP-style first PoC transport direction
- `PlayerCommandFrame` and snapshot/delta DTOs
- a dedicated-server host direction

What is still missing is the protocol contract layer between:

- raw transport bytes
- serialized envelopes
- runtime/gameplay commands and replicated results

## Recommended Layering

The client/server communication stack should be split into four layers.

### 1. Transport layer

Responsibilities:

- open and close connections
- send and receive framed payloads
- report connection lifecycle events

Recommended interfaces:

- `IClientTransport`
- `IServerTransport`
- `ITransportConnection`

This layer should not know what a `PlayerCommandFrame` means.

### 2. Framing and codec layer

Responsibilities:

- convert messages to and from bytes or text payloads
- delimit individual protocol messages on a stream transport
- enforce protocol version and message kind metadata

Recommended interfaces:

- `IProtocolCodec`
- `IMessageFramer`

Recommended first implementation types:

- `TransportJsonCodec`
- `LengthPrefixedMessageFramer` or equivalent stream-safe framing helper

### 3. Protocol message layer

Responsibilities:

- define allowed client-to-server and server-to-client message families
- carry serializable protocol DTOs
- keep message contracts independent of gameplay implementation classes

Recommended interfaces:

- `IProtocolMessage`
- `IClientToServerMessage`
- `IServerToClientMessage`

### 4. Runtime adapter layer

Responsibilities:

- translate protocol messages into runtime actions
- translate runtime snapshots/results into outbound protocol messages
- keep `WorldSessionRuntime` free of transport concerns

Recommended interfaces:

- `IClientMessageHandler`
- `IServerMessageHandler`
- `IProtocolRouter`

## Recommended Message Families

The first protocol should stay deliberately small.

### Client to server

- `ClientHelloMessage`
- `ClientJoinRequestMessage`
- `PlayerCommandMessage`
- `ClientDisconnectMessage`
- optional `ClientPingMessage`

### Server to client

- `ServerHelloMessage`
- `JoinAcceptedMessage`
- `JoinRejectedMessage`
- `ServerSnapshotMessage`
- `ServerActionMessage`
- optional `ServerPingReplyMessage`
- optional `ServerDisconnectMessage`

## Action Contract Pattern

The protocol should not send dozens of ad hoc action flags at top level.

Instead, action-oriented traffic should follow a focused interface-driven pattern:

- one envelope/message family for "client submitted an action"
- one explicit DTO family for the action payload
- one explicit result family for authoritative server outcomes

Recommended shapes:

- `IPlayerActionPayload`
- `BuildUseIntent`
- `CombatUseIntent`
- `AuthoritativeActionResult`

This keeps the protocol extensible without turning it into a stringly typed command bus.

## Envelope Pattern

The first protocol should use one explicit envelope with stable metadata such as:

- protocol version
- message kind
- session or connection identifier where appropriate
- correlation id or sequence id where appropriate
- typed payload

Recommended first type:

- `TransportEnvelope`

This envelope should be a protocol concern, not a gameplay concern.

## Routing Pattern

Avoid large `switch` blocks scattered across hosts, scenes, and runtime types.

Recommended protocol routing pattern:

- `IProtocolRouter`
- focused handlers per message family

Examples:

- `JoinRequestHandler`
- `PlayerCommandHandler`
- `SnapshotMessageHandler`
- `ServerActionMessageHandler`

These may start small, but the design should allow message handling to stay decomposed as the protocol grows.

## Clean-Code Rules

### Use interfaces where behavior varies by role

Good interface candidates:

- codec
- framer
- router
- transport connection
- action payload or action result families

### Avoid interfaces for passive DTOs

Simple DTOs such as snapshots, join payloads, and mutation records do not all need behavior-heavy interfaces.

The rule should be:

- interfaces for seams and behavior
- concrete records/DTOs for serializable data

### Keep runtime actions separate from wire messages

`PlayerCommandFrame` may be embedded in or referenced by a protocol message, but the runtime should not depend on socket envelope types directly.

## Protocol Versioning

Even for the first PoC, include a protocol version field in the envelope or handshake path.

This keeps later changes manageable and avoids hidden compatibility assumptions between server and client builds.

## Repo-Specific Hazards

### Letting `PlayScene` become a protocol router

That would repeat the same over-centralization the repo is already working to reduce.

### Letting `WorldSessionRuntime` depend on envelope classes

That would leak protocol concerns into simulation.

### Using one giant message class with nullable fields

That would be easy to start but hard to reason about or test.

### Treating local scene events as the wire protocol

`SceneEventHub` is valuable locally, but protocol messages should be explicit and versioned.

## Recommendation

The protocol direction is sound at a high level, but it needs its own explicit ledger contract.

Use:

- narrow transport interfaces
- explicit codec/framing interfaces
- typed client/server message families
- focused protocol routers and handlers
- concrete action payload/result DTOs

That is the cleanest protocol shape for the current `dream` multiplayer roadmap.
