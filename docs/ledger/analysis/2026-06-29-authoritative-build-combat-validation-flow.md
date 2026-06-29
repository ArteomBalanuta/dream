# Authoritative Build Combat Validation Flow

Purpose: define the authoritative action flow for building and combat in multiplayer so local intent becomes validated server-owned world mutation and replicated outcome.
Status: complete
Scope: build intent, combat intent, server validation, authoritative result objects, correction flow, and local client rollback/confirmation behavior

## Goal

Make the current build and combat systems compatible with dedicated-server authority.

The multiplayer action flow should become:

1. client captures local intent
2. client sends command/use payload
3. server validates the action against authoritative state
4. server applies accepted mutation or rejects it
5. server replicates the result
6. client confirms or corrects local assumptions

## What Exists Today

The current local flow is scene-friendly and direct:

- `PlayerInteraction` captures primary use edges
- placement calls `map.AddCube(...)` directly
- combat feedback is triggered from local scene logic
- `PlayerCombatant` owns health and damage participation
- `SceneEventHub` publishes local domain facts for local reactions

That is fine for single-player, but it is not suitable for shared multiplayer truth.

## Required Multiplayer Shift

### Client-side role

The client should:

- decide what the local player is trying to do
- package that as build or combat intent
- optionally predict limited feel-sensitive behavior
- wait for authoritative confirmation or correction

### Server-side role

The server should:

- validate whether the requested action is legal
- decide whether it succeeds or fails
- mutate the authoritative world/combat state
- emit explicit result objects and replication payloads

## Recommended New Contracts

### Intent contracts

- `BuildUseIntent`
- `CombatUseIntent`
- `ValidatedUseRequest`

These separate "what the player asked for" from "what the server accepted."

### Authoritative result contracts

- `AuthoritativeActionResult`
- `WorldMutationResult`
- `CombatResolutionResult`
- `RejectedUseDelta`

These separate validation from mutation and replication.

### Client correction contracts

- `ClientCorrectionDelta`
- `LocalActionHistory`
- `ClientCorrectionApplier`

These allow the client to reconcile local assumptions with server truth.

## Build Validation Flow

For building, the server should validate:

- requested target cell
- bounds
- occupancy
- placement legality relative to current authoritative map state

If valid:

- create one `WorldMutationResult`
- apply the mutation to authoritative world state
- include the result in outbound replication

If invalid:

- create one rejection/correction payload
- do not mutate the authoritative world

## Combat Validation Flow

For combat, the server should validate:

- requested target or hit context
- whether the action is legal at the current tick
- whether the target remains valid on the authoritative state

If valid:

- apply damage through authoritative combat state
- produce `CombatResolutionResult`
- replicate damage and death outcomes

If invalid:

- reject the action without local gameplay truth changing

## Client Correction Rules

The first correction model should be narrow.

Good early correction cases:

- predicted local cube placement rejected by server
- conflicting simultaneous placement resolved against another client
- local combat swing predicted but server denies hit

The client should not try to predict everything. It should only predict where responsiveness matters and rollback is understandable.

## Relationship To Existing Types

### `PlayerInteraction`

Should stop being the place where authoritative placement happens.

Instead it should:

- detect or package local use intent
- hand that intent off to the runtime/transport path

### `Map`

Should only mutate authoritatively in multiplayer mode.

Direct scene-local `AddCube(...)` ownership is the main multiplayer hazard here.

### `PlayerCombatant`

Should stay a clean combat-state seam, but multiplayer combat facts must come from server-authoritative resolution rather than local presentation flow.

### `SceneEventHub`

Should continue to power local reactions such as HUD/combat feedback, but those reactions should consume authoritative deltas instead of acting as the source of truth.

## Diagnostics Direction

This slice should include action-flow diagnostics such as:

- session id
- command sequence
- server tick
- validation verdict
- accepted mutation or rejection
- client correction applied

Suggested helpers:

- `AuthoritativeFlowTrace`
- `MultiplayerDiagnostics`

## Repo-Specific Hazards

### `PlayerInteraction` currently calls `map.AddCube(...)` directly

This is the clearest immediate ownership violation for multiplayer.

### `PlayScene` currently drives local combat feedback from local use flow

That risks accidentally preserving local combat authority even after replication exists.

### `Map.AddCube(...)` is permissive

Without a validation gate, the client could keep mutating local world state outside the server path.

### `SceneEventHub` is local by design

It should not be mistaken for the replication layer itself.

## Recommendation

Add explicit validation, result, and correction contracts for building and combat before trying to polish prediction.

That keeps the multiplayer action path honest: local intent is cheap, but authoritative mutation belongs to the server.
