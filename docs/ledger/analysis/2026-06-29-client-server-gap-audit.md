Purpose: audit the current multiplayer client/server split for missing synchronization, missing replicated facts, and local-only gameplay state that still bypasses authority.
Status: complete
Scope: protocol surfaces, snapshot contents, HUD/runtime stat ownership, one-shot feedback/event paths, and settings-driven gameplay state

# Summary

The current multiplayer stack has a workable transport and snapshot foundation, but several gameplay-facing facts are still only partially synchronized between client and server.

The most important gap that directly affected correctness was player combat stats: the client HUD could mutate `Strength`, `Attack Speed`, and `Vitality` locally, but the authoritative server had no protocol path to receive or republish those changes.

This pass closes that gap with:

- an explicit player-stats update intent/result on the authoritative action channel
- authoritative player-stat replication on player actor snapshots
- client application of authoritative stat snapshots/results back into the local runtime

# Current Gap Inventory

## Fixed In This Pass

### 1. Player combat stats were client-local only

- Client-side settings edits in `PlayScene.ApplyHudRequest(...)` called `PlayerCombatant.ApplyStats(...)` locally.
- The multiplayer protocol had no message for stat updates.
- `ActorSnapshot` did not carry authoritative player combat stats, so join/resync could not correct HUD or local runtime values.

Result before fix:

- local HUD damage ranges and HP derivation could diverge from server combat truth
- authoritative remote damage/cooldown behavior still used server defaults
- join/snapshot correction could not recover from stat drift

### 2. Payload polymorphism used a conflicting discriminator name

- `IPlayerActionPayload` and `IActionResultPayload` used `kind` as the JSON discriminator.
- Some payload types already expose a `Kind` property, such as `WorldMutationResult`.

Result before fix:

- expanding the typed action/result payload set further would break serialization

## Remaining Gaps

### 1. Command-driven primary-use actions still do not produce a dedicated per-action acknowledgement path

Primary-use actions sent through `PlayerCommandFrame.PrimaryUsePressed` are processed inside the server tick. Their accepted world/combat outcomes currently surface through snapshot deltas rather than through a dedicated `server.action` reply tied to the originating command sequence.

Current effect:

- build/combat outcomes can be authoritative and visible, but not every command-driven use has a one-shot action acknowledgement contract
- local UX for accepted build/combat actions still relies on snapshot timing instead of an explicit action response

### 2. Several gameplay-affecting settings are still local-only in remote mode

Examples:

- floor-size apply
- noclip/fly speed if remote noclip is used

These settings still mutate local runtime state or local UI state without a corresponding authoritative server update contract.

Current effect:

- remote clients can still create local-only expectations about gameplay-affecting settings
- some settings should likely be disabled in remote mode unless protocolized intentionally

### 3. Snapshot damage feedback still drops critical-hit style information

`DamageEventDelta` carries damage and health outcome facts but not floating-text kind.

Current effect:

- snapshot-rehydrated combat feedback can restore immediate local text presentation
- critical-hit styling is not yet preserved through the snapshot damage path

### 4. Secondary remote player stats are not surfaced in a presentation-facing way

The authoritative snapshot now has the data needed, but the current client presentation path does not use remote player combat stats for any UI or debug view.

Current effect:

- correctness is improved for authoritative server combat ownership
- observer/debug visibility of other players' stat state is still minimal

### 5. Join/startup hydration is still broader for world state than for all future player-owned config categories

The current join path now hydrates floor/world state and can hydrate player combat stats through actor snapshots, but any future player-owned config values will need an explicit decision:

- snapshot-carried and authoritative
- local-only and presentation-only
- command/action replicated with server ownership

# Recommended Direction

## Immediate protocol rule

If a client-side UI control changes gameplay truth, it should follow one of these patterns:

- explicit authoritative action/update request plus snapshot/result correction
- snapshot-only authoritative replication if the value changes elsewhere and the client never authors it

Avoid mixed ownership where local runtime mutates gameplay state but the server remains unaware.

## Current best-fit categories

- presentation-only settings:
  - field of view
  - mouse sensitivity
  - floating text tuning
  - resolution

- authoritative gameplay settings/state:
  - combat stats
  - interaction mode
  - world occupancy
  - floor size if changed during multiplayer
  - noclip speed if remote server-controlled flying remains supported

# Outcome

The multiplayer model is now materially cleaner for player combat stats, but the broader repo still needs a consistent policy for which settings are purely client presentation and which must be routed through authority and snapshots.
