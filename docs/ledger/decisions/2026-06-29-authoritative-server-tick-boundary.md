# Authoritative Server Tick Boundary

Purpose: record the durable multiplayer architecture rule for server authority, tick ownership, and client-local presentation boundaries.
Status: accepted
Scope: dedicated server ownership, 64 Hz tick cadence, and the line between authoritative simulation and local presentation

## Decision

`dream` will pursue multiplayer through one authoritative dedicated server process hosting one shared world session, with simulation advanced on a fixed 64 Hz server tick.

The authoritative server owns gameplay truth. Clients may predict or smooth selected behavior, but client-side presentation does not override server results.

## Rationale

- the project already has enough structure to extract a shared simulation runtime from `PlayScene`
- a dedicated server model fits the long-term goals of many clients in one shared world better than peer-to-peer assumptions
- a fixed tick gives the repo a clear timing contract for commands, physics, combat, and replication
- leaving presentation-heavy systems client-local keeps the first PoC practical

## Consequences

### Server-owned

- player session identity
- world state truth
- physics stepping
- authoritative transforms
- build/combat acceptance
- actor lifecycle
- replication sequencing

### Client-local

- input capture
- camera presentation
- HUD and ImGui behavior
- first-person viewmodels
- floating combat text rendering
- smoothing and interpolation

### Hybrid

- prediction and correction for feel-sensitive movement or use actions

## Non-Decisions

This decision does not lock:

- the final wire protocol
- the final transport library
- a persistence model
- multi-room hosting
- an anti-cheat system beyond basic server authority

## Follow-Up Rule

When future code introduces new multiplayer-aware systems, default the question to:

1. does this affect authoritative gameplay truth
2. if yes, it belongs on or derives from the server tick path
3. if no, keep it client-local unless a later need proves otherwise

This rule should keep the networking refactor from collapsing back into a mixed local scene model.
