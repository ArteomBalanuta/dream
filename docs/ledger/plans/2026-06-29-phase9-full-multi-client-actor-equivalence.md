# Phase 9 Full Multi Client Actor Equivalence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the current "one full player plus lightweight secondary actors" asymmetry so all joined clients participate through equivalent authoritative player ownership.

**Architecture:** Extend the runtime so each joined player owns a full authoritative player actor contract, then update replication, action validation, and gameplay systems to stop special-casing only the primary controlled player path.

**Tech Stack:** C# / .NET 10, existing authoritative runtime, session registry, command frames, snapshot DTOs, and multiplayer action contracts

---

## File Structure Direction

- `dream/WorldSessionRuntime.cs`
  - multi-player authoritative actor ownership
- `dream/PlayerSessionRegistry.cs`
  - actor/session mapping stays authoritative
- `dream/RemoteSessionPlayerState.cs` or successor
  - may be removed or reduced once full equivalence exists
- `dream/ActorSnapshot.cs`
  - snapshot continuity across all players

### Task 1: Audit primary-player special cases

**Files:**
- Modify: `docs/architecture.md`
- Create: `docs/ledger/analysis/2026-06-29-primary-player-special-cases.md`

- [ ] enumerate where only the primary player owns full authoritative behavior
- [ ] separate presentation-only assumptions from simulation assumptions

### Task 2: Promote joined players to equivalent authoritative actor ownership

**Files:**
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/PlayerSessionRegistry.cs`
- Modify: `dream/Player.cs`
- Modify: `dream/PhysicsIntegrator.cs`

- [ ] remove one-player-only simulation assumptions
- [ ] allow each joined session to map to equivalent authoritative player ownership
- [ ] keep replication DTO contracts clean while changing runtime depth

### Task 3: Re-verify build/combat/replication for all players

**Files:**
- Modify: `dream.Tests/*multiplayer*.cs`
- Create: `docs/ledger/work-log/2026-06-29-full-multi-client-actor-equivalence.md`

- [ ] cover movement, build, combat, and snapshot behavior for non-primary joined players
- [ ] document remaining non-goals honestly if PvP or room management still stays out of scope

## Self-Review Notes

- Spec coverage: addresses the largest remaining simulation-side multiplayer gap
- Placeholder scan: tasks are concrete without pretending this is already implemented
- Type consistency: continues the existing `WorldSessionRuntime` plus session-registry model instead of inventing a separate host stack
