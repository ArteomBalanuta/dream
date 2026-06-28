# Creature Support Foundation Plan

Date: 2026-06-28
Status: Planning
Owner: Codex

## Goal

Introduce a repo-native combat capability layer so players, future enemies, and damageable objects can participate in shared combat flows without forcing the project into a Unity-style inheritance tree.

## Core Principle

The project should support “creatures” as a capability, not as a deep engine-bound base class.

That means:

- prefer interfaces and small data holders
- keep scene ownership explicit
- allow multiple target kinds to share combat participation

## Recommended Architecture

### Shared Contracts

- `IDamageable`
  - accepts damage input
- `ICombatAnchorProvider`
  - provides a world position for hit feedback such as floating text

### Shared Runtime Identity

- `CreatureKind`
  - `Player`
  - `Enemy`
  - `Object`

- `CreatureHandle` or `CombatActorHandle`
  - lightweight identity plus kind and anchor access

### Shared State Components

- `HealthState`
  - current/max health
- later optional `CombatStats`

## Initial Integration Targets

- `Player`
- future enemy entities
- breakable objects or destructible props

## Delivery Steps

1. Introduce a minimal shared combat contract.
2. Add a player-side implementation first.
3. Add explicit anchor support for hit feedback.
4. Add scene-owned registration or lookup as needed.
5. Keep enemy-specific logic out until enemies actually exist.

## Success Criteria

- the player can participate in shared combat interfaces without architecture strain
- non-player damageable objects can be added later without refactoring the entire combat path
- floating combat text has a reusable combat-anchor source

## Deferred

- boss/minion inheritance trees
- state machine standardization
- AI combat behavior
- full death/event pipelines across many entity types

## Notes

This plan is intentionally smaller than the original `Creature` hierarchy proposal. The main goal is to create a clean support layer for combat features, not to fully define the entire game's entity taxonomy in one pass.
