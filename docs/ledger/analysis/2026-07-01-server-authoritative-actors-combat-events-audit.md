Purpose: audit how the server currently treats players, enemies, combat actions, damage events, and HP replication
Status: complete
Scope: authoritative runtime behavior for player/enemy actors, PvE, PvP readiness, multi-client enemy attacks, event ordering, and HP streaming

## Summary

The engine already has a shared authoritative world with shared players, enemies, health, and snapshot replication. Multiple clients connected to the same server observe the same `WorldSessionRuntime`, the same wolf, and the same actor health values.

The current implementation is not yet a full event-queued combat engine. It is closer to:

- latest movement command per player per server tick
- immediate authoritative action processing for `player.action`
- host-level locking to serialize concurrent action handlers
- pending world/damage deltas accumulated until the next snapshot export

That means the server can handle many practical race cases safely for the current PoC, but the semantics are arrival-order based rather than an explicit deterministic event queue with event ids, source actor ids, target actor ids, and server-tick ordering.

## Current Actor Model

### Players

Players are represented in two depths:

- The first controlled session maps to the full Bullet-backed `Player` path.
- Additional joined sessions use `RemoteSessionPlayerState`.

Both paths expose `PlayerCombatant`, health, actor id, position, facing, interaction mode, and authoritative target data through `IAuthoritativePlayerTarget`.

Players are damageable. `PlayerCombatant` implements `IDamageable`, owns `HealthState`, and publishes death events when health reaches zero.

### Enemies

Enemies are held by `EnemyRegistry` as `EnemyActor` instances. The current wolf uses `EnemyCombatant`, `HealthState`, actor id `2000`, display name `Wolf`, and explicit collision/attack settings.

Enemy AI already targets authoritative player targets, not only the original local player. `EnemyRegistry.UpdateAll(...)` chooses the nearest alive spawned player target, and `EnemyActor.TryAttack(...)` applies damage to that target's `PlayerCombatant`.

### World Objects

Placed cubes are represented as combat-capable world objects through `WorldObjectRegistry`. They can take damage in combat mode. The base floor remains protected.

## Scenario Answers

### Can players damage enemies like the wolf?

Yes.

For authoritative combat actions, `WorldSessionRuntime.ProcessAuthoritativeAction(...)` resolves an action context for the acting session and routes `CombatUseIntent` into `ProcessCombatUseAction(...)`. That flow:

- verifies the acting session is in combat mode
- checks the acting combatant's attack cooldown
- resolves enemy hits before destructible cube hits
- applies damage to `EnemyCombatant`
- publishes `DamageAppliedEvent`
- returns `CombatResolutionResult` with target actor id, target kind, damage amount, death flag, and remaining HP

This works for secondary remote sessions too. Existing tests already cover secondary remote sessions resolving combat damage through authoritative actor aim.

### Can players damage other players?

Not as an intentional player attack path yet.

The engine has enough primitives for PvP because players are `IDamageable`, have `CombatActorHandle`, and appear in actor snapshots with health. However, player combat targeting currently checks:

1. enemies
2. destructible cubes

It does not raycast or resolve other `IAuthoritativePlayerTarget` instances as attack targets. Therefore PvP should be treated as not implemented, even though the health model can support it.

### Can enemies damage players?

Yes.

Enemy AI picks the nearest alive authoritative player target and applies damage through `EnemyActor.TryAttack(...)`. This includes secondary remote players. Existing coverage verifies a wolf can damage the nearest secondary session while leaving the farther primary session untouched.

### If two players attack the same enemy, does the server register both hits?

Conditionally yes, but with current PoC semantics rather than a durable event queue.

If both `player.action` messages are accepted and the target is still alive when each action is processed, each hit mutates the same authoritative enemy health. The host serializes inbound message handling under `_authoritySync`, so the health mutation itself does not run concurrently.

Important limits:

- Order is based on connection/message arrival under the host lock, not on an explicit queued event timestamp.
- `PlayerActionMessage` has session id and action sequence, but no client input timestamp or intended server tick.
- `DamageEventDelta` has target actor id, amount, death flag, and remaining HP, but no event id, source actor id, source session id, action sequence, or server tick.
- If the first accepted hit kills and prunes the enemy, the second action may resolve no target depending on order.
- Attack cooldown is per acting combatant, so two different players can both hit if their individual cooldowns allow it.

For a real multiplayer engine, this should become an authoritative tick event queue so multiple attacks against one enemy are ordered, applied, and replicated as explicit combat facts.

### Are enemy HP updates streamed to each player?

Yes, through two mechanisms:

- `ActorSnapshot` includes `CurrentHealth`, `MaxHealth`, `IsAlive`, `Kind`, position, facing, and display name for active players and enemies.
- `ServerSnapshotEnvelope` includes `DamageEvents`, and `DedicatedServerService.AdvanceOnceAsync(...)` broadcasts each snapshot to all joined connections.

Client-side remote enemy state is rebuilt from authoritative enemy actor snapshots, so each client should converge on the same wolf HP. Damage feedback also flows through snapshot damage events, though it currently loses details such as crit style because `DamageEventDelta` does not carry floating-text kind.

### Are HP updates streamed for players?

Partly yes.

Player `ActorSnapshot` records current and max health for each player actor. The local controlled player's authoritative health is applied through client correction. Remote player presentation has snapshot health available, but richer remote player health UI and PvP-specific feedback are not yet a complete product feature.

## Engine Gaps

### 1. No durable combat event queue

The server currently processes `player.action` immediately when a connection receives it. This is protected by a host lock, but it is not the same as queueing all actor actions and resolving them at a specific authoritative tick.

Risk:

- race order is arrival-order, not tick-order
- action facts are hard to replay
- damage feedback lacks stable event identity
- tests cannot assert deterministic ordering for simultaneous attacks except through manually chosen call order

### 2. Combat target resolution is not unified

The engine has reusable actor concepts, but hit resolution still branches by target category. Enemy and object targeting are supported; player targeting is not.

Risk:

- PvP requires new special-case logic unless we introduce a unified resolver
- friendly fire rules have no clean home
- target priority is implicit instead of policy-driven

### 3. Damage deltas are too thin

`DamageEventDelta` is useful for basic floating damage text, but it lacks:

- event id
- server tick
- source session id
- source actor id
- target actor id is present, but target display name is not
- action sequence
- floating text kind, including crit
- damage cause

Risk:

- clients cannot deduplicate damage events robustly
- clients cannot show source-aware combat logs
- crit feedback can regress to generic damage text
- late snapshots cannot be reasoned about as an ordered event stream

### 4. Action acceptance is separate from tick application

Movement commands are drained per tick, while action messages are processed immediately. That split is acceptable for the PoC, but it makes the engine harder to reason about once combat involves multiple clients, animation timing, PvP, latency compensation, or authoritative rewind.

Risk:

- a hit can be applied between ticks using actor state from the last applied command
- two clients can perceive "same time" attacks, but the server resolves them by arrival order
- action result server tick may be current tick, not necessarily the tick where the action was queued for deterministic simulation

## Recommended Direction

Move toward one authoritative actor event pipeline:

1. Clients send intent messages.
2. Server validates session ownership and action sequence.
3. Server enqueues accepted intents into an authoritative per-tick action queue.
4. The 64 Hz tick drains queued actions in deterministic order.
5. Combat resolver resolves targets from one actor registry.
6. Health mutations publish durable combat events.
7. Snapshots stream both current actor state and ordered event deltas to all joined clients.

This keeps the server as the single truth while giving clients enough event detail for smooth feedback, combat logs, death handling, and eventual prediction/reconciliation.

## Recommended Target Policy

### Targeting

Use a single `AuthoritativeCombatTargetResolver` that can resolve:

- players
- enemies
- destructible world objects

Target priority should be explicit:

1. living enemy actors
2. living player actors if PvP is enabled
3. destructible world objects

PvP should start disabled by default and be enabled through a server/runtime rule, not by client trust.

### Event ordering

Use deterministic ordering inside each server tick:

1. server tick
2. server receive order assigned by the host
3. session id as tie-breaker
4. action sequence as final tie-breaker

Do not trust client timestamps for ordering in the first implementation.

### Replication

Keep current actor snapshots because they are the state baseline. Add richer combat event deltas for edge-triggered feedback:

- event id
- server tick
- source actor id
- source session id
- target actor id
- target kind
- damage amount
- target remaining HP
- target max HP
- death flag
- floating text kind
- action sequence

Clients should apply actor snapshots for authoritative health and use combat events for presentation.

## Conclusion

The current server is already good enough for a shared-world PvE PoC: many clients can damage the shared wolf, enemy HP is replicated, and enemy attacks can damage multiple player targets through authoritative state.

The missing engine-level piece is not "health exists" or "snapshots exist." The missing piece is a deliberate combat event queue and a unified target resolver. That is the step that will make simultaneous attacks, PvP, richer enemy groups, and robust client feedback feel like an engine instead of a collection of special cases.
