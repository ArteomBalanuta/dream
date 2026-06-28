# Combat Stats Framework Plan

Date: 2026-06-28
Status: Planning
Owner: Codex

## Goal

Add a compact stat and modifier framework only after creature support and the first combat feedback loop are in place.

## Why This Is Deferred

The generated proposal's stat system is conceptually useful, but this repo does not yet need the full matrix immediately.

Starting too early would risk:

- overbuilding
- unclear ownership
- lots of code without gameplay validation

## Recommended First-Cut Scope

Start with only the stats required by the first real combat loop:

- max health
- current health
- optional base damage if attack resolution lands next
- optional attack interval if combat cadence needs it

## Recommended Architecture

- `CombatStat`
  - base value, modifier list, cached final value
- `CombatStatModifier`
  - flat or percent
- `CombatStatType`
  - only types actually used in-game

Avoid the full proposal's complete RPG matrix until those stats have consumers.

## Delivery Steps

1. Identify the minimal stats required by the first combat milestone.
2. Implement a small cached stat value object.
3. Add only the modifier categories actually needed.
4. Integrate with player or damageable actor support.
5. Expand later if equipment, buffs, or status effects become real gameplay systems.

## Success Criteria

- stats solve a real combat use case
- no dead framework code exists only for future possibility
- the system can expand later without rewriting the core

## Deferred

- full attack speed animation coupling
- min/max damage range system
- complex item/equipment integration
- shield-defense/PvP layers
- large status-effect matrix

## Notes

This track should begin only after the project has at least one stable combat exchange path. Until then, the framework should stay intentionally small.
