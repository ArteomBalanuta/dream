# Multi Client Shared World And Player Identity Audit

Purpose: document what the current multiplayer implementation already shares correctly across two or more clients and isolate the remaining player-identity gaps.
Status: complete
Scope: authoritative world ownership, enemy/world replication, session registry coverage, and missing player-name/roster replication

## Summary

The current runtime already hosts one shared authoritative world session per server host.

If two clients connect to the same authoritative server:

- they share the same `WorldSessionRuntime`
- they observe the same authoritative cube world
- they observe the same authoritative enemy set, including the current wolf
- they receive snapshots exported from the same runtime tick stream

That means the current answer to "do both clients share the same wolf and world?" is yes.

## What Is Already Shared

### Shared world state

`RuntimeAuthoritativeSessionHost.CreateDefault()` constructs one `WorldSessionRuntime`, and that runtime owns:

- `Map`
- `EnemyRegistry`
- `PhysicsIntegrator`
- `PlayerSessionRegistry`

`DedicatedServerHost` then advances that one runtime and exports snapshots from it for all joined connections.

### Shared enemy state

`WorldSessionRuntime.ExportServerSnapshotEnvelope(...)` exports:

- all authoritative player actor snapshots
- all active enemy actor snapshots
- world mutation deltas
- damage event deltas

So enemy life, movement, and death are not client-local illusions. They are server-owned facts that every connected client can observe through snapshots.

### Shared session registry

The repo already has a real `PlayerSessionRegistry`.

Today it tracks:

- `PlayerSessionId`
- authoritative `ActorId`
- local vs remote ownership
- spawned state
- last accepted command sequence
- display name at the registry layer

So the answer to "do we have a player registry?" is also yes.

## Remaining Identity Gaps

### 1. Registry identity is deeper than replicated identity

The server registry can now retain a player's display name, but the replicated actor snapshot shape still does not carry player names.

That means:

- the server knows a joined player's name
- the client does not yet receive a clean replicated roster/name view
- remote nameplates, player lists, and richer connection UI still need a later protocol slice

### 2. Session ownership is still asymmetric

The first joined remote session still aliases the full authoritative `Player` actor, while later sessions remain lighter-weight remote actor states.

This is workable for the current PoC, but it still means "many connected clients" is ahead of "full player equivalence."

### 3. Join acknowledgement is world-first, not roster-first

`JoinAcceptedMessage` currently returns:

- session id
- actor id
- current server tick
- initial world snapshot

It does not yet include:

- joined player roster
- player display names
- actor-to-name mapping

## Recommended Next Identity Slice

The next clean multiplayer identity phase should add:

- a replicated player roster DTO
- authoritative actor-to-display-name mapping
- snapshot or join-time roster hydration
- client-side remote-player presentation that can consume names without digging into transport internals

## Conclusion

The server already behaves as one shared authoritative world with one shared wolf/world state for all connected clients.

The missing part is not "shared world existence." The missing part is "replicated player identity and roster presentation."
