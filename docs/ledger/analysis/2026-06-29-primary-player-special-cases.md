# Primary Player Special Cases

Purpose: audit the remaining places where multiplayer runtime behavior still privileges the primary controlled player path over other joined sessions.
Status: complete
Scope: authoritative runtime ownership, enemy targeting, interaction-mode ownership, client correction assumptions, and transition-safe cleanup priorities

## Summary

`dream` already supports many joined sessions, but the runtime still has a mixed-depth model:

- the first joined remote session aliases the full authoritative `Player` actor
- later joined sessions use `RemoteSessionPlayerState`

That asymmetry is acceptable for the current PoC, but it still leaves several primary-path assumptions that should be treated deliberately instead of being rediscovered later.

## Current Special Cases

### 1. One full `Player` path still exists

`WorldSessionRuntime` still owns one Bullet-backed `Player` instance as the deepest authoritative player representation.

That means:

- local presentation still composes around one camera/body pair
- the first remote-controlled joined session reuses that actor
- later sessions remain lighter-weight actor state

### 2. Interaction mode still has two shapes

The primary actor still uses runtime-global `_interactionMode`, while secondary actors carry their own mode on `RemoteSessionPlayerState`.

This is workable during the transition, but it means the protocol and runtime should keep distinguishing:

- transitional controlled-actor/global mode facts
- durable per-actor replicated mode facts

### 3. PvE used to be single-player-centric

Before the current follow-up slice, enemy aggro/attack/separation only consumed:

- one `playerPosition`
- one `PlayerCombatant`

That created a real shared-world asymmetry because joined secondary actors could move and act, but world PvE still treated only the primary actor as a valid target.

The current runtime follow-up replaces that with a shared authoritative player-target seam so enemies can choose among joined player actors instead of assuming exactly one player target.

### 4. Client correction still assumes one controlled runtime actor

The connected desktop client still applies authoritative correction to the locally controlled runtime `Player` object.

That is still acceptable because the local desktop client controls one session actor at a time, but it is worth recording as a controlled assumption rather than pretending the client runtime is already fully session-symmetric.

## Recommended Cleanup Order

### First

Keep reducing runtime branching through shared session-actor seams such as:

- authoritative player-target lookup
- session-to-actor resolution
- actor snapshot export
- enemy targeting and collision separation

### Next

Document which fields remain transition-only:

- global snapshot interaction mode
- primary controlled camera/body assumptions
- local client correction target rules

### Later

Only after those seams are stable should the repo take on a larger folder/namespace or module reorganization for multiplayer.

That keeps the structural cleanup attached to proven runtime boundaries instead of moving files before the boundaries are actually trustworthy.
