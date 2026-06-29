# Player Registry And Name Replication Implementation Plan

Purpose: define the next clean-code multiplayer slice for turning server-side player registry identity into replicated client-visible player names and roster data.
Status: proposed
Scope: registry model, join/snapshot protocol, client roster hydration, and presentation-facing seams

## Goal

Keep `PlayerSessionRegistry` as the authoritative server-side player directory, then add explicit protocol and client seams so joined players can be identified by name across clients.

## Current Baseline

Already present:

- `PlayerSessionRegistry`
- `ConnectedPlayer`
- stable `PlayerSessionId`
- session-to-actor mapping
- snapshot export for player actors
- join flow with requested display name

Still missing:

- roster replication DTOs
- actor/name replication to clients
- client-owned roster cache
- any player-name presentation seam

## Task 1: Stabilize authoritative registry identity

Files:

- `dream/ConnectedPlayer.cs`
- `dream/PlayerSessionRegistry.cs`
- `dream/WorldSessionRuntime.cs`

Steps:

- [ ] keep `ConnectedPlayer` as the authoritative identity source for session metadata
- [ ] ensure local and remote sessions always resolve a normalized display name
- [ ] avoid leaking transport concerns into the registry type

## Task 2: Add explicit roster replication contracts

Files:

- `dream/PlayerRosterEntry.cs`
- `dream/JoinAcceptedMessage.cs`
- `dream/ServerSnapshotEnvelope.cs`
- `dream/ActorSnapshot.cs`
- `dream/TransportJsonCodec.cs`

Steps:

- [ ] define a small roster DTO for `SessionId`, `ActorId`, and `DisplayName`
- [ ] choose whether roster data belongs in `JoinAcceptedMessage`, snapshots, or both
- [ ] keep player identity replication separate from purely gameplay-facing actor state

Recommended direction:

- join-time full roster
- later snapshot deltas only when roster changes

## Task 3: Add client roster hydration and update seams

Files:

- `dream/ClientJoinAcceptedApplier.cs`
- `dream/ClientProtocolSession.cs`
- `dream/PlayScene.cs`
- `dream/ClientScenePresenter.cs`

Steps:

- [ ] hydrate a client-side roster cache on join
- [ ] update that cache from later authoritative messages
- [ ] expose roster lookups through scene/presenter seams instead of direct protocol reads

## Task 4: Add player-name presentation seams

Files:

- `dream/RemotePlayerRenderer.cs`
- `dream/HudRenderer.cs`
- `dream/Render.cs`

Steps:

- [ ] keep world-space actor rendering independent from transport DTOs
- [ ] add optional remote-player nameplate or roster panel support
- [ ] ensure the local camera remains client-only and is not network-owned

## Task 5: Cover the protocol and edge cases with tests

Files:

- `dream.Tests/ProtocolFoundationTests.cs`
- `dream.Tests/ClientAuthoritativeStateApplierTests.cs`
- `dream.Tests/PlayerSessionRegistryTests.cs`
- `dream.Tests/ClientScenePresenterTests.cs`

Steps:

- [ ] verify display-name round-tripping over protocol
- [ ] verify duplicate session rejoin keeps stable identity rules
- [ ] verify blank names normalize safely
- [ ] verify client-side roster hydration after join

## Success Criteria

- the server retains one authoritative player registry
- every connected client can resolve player names consistently
- player names are replicated through explicit DTOs rather than inferred from ad hoc transport state
- rendering/UI consumes client-side roster state through clean interfaces
