# Phase 3 Server Tick And Snapshot Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish the 64 Hz authoritative tick contract and the first explicit snapshot/delta export layer needed for a dedicated-server multiplayer proof of concept.

**Architecture:** Add fixed server tick scheduling around `WorldSessionRuntime`, define outbound snapshot and mutation DTOs, and keep those replication contracts separate from transport details and renderer-only state.

**Tech Stack:** C# / .NET 10, BulletSharp, current combat/world model, future transport edge chosen later

---

### Task 1: Add fixed tick scheduling primitives

**Files:**
- Create: `dream/SimulationClock.cs`
- Create: `dream/ServerTickRunner.cs`
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/PhysicsIntegrator.cs`

- [ ] Define a 64 Hz fixed-step contract for the authoritative runtime
- [ ] Ensure physics stepping and simulation ordering run against tick cadence instead of client render cadence
- [ ] Keep the current desktop host able to drive the runtime during transition, even if the full dedicated server path is not present yet
- [ ] Avoid mixing interpolation or smoothing rules into the tick runner

### Task 2: Define outbound snapshot DTOs

**Files:**
- Create: `dream/ActorSnapshot.cs`
- Create: `dream/WorldSnapshot.cs`
- Create: `dream/ServerSnapshotEnvelope.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Define the minimum authoritative actor state required for remote presentation and local correction
- [ ] Define a world snapshot suitable for join/resync use
- [ ] Include server tick metadata in the snapshot envelope
- [ ] Keep the DTOs independent of OpenTK window/input types and renderer-facing state

### Task 3: Define world and combat delta DTOs

**Files:**
- Create: `dream/WorldMutationDelta.cs`
- Create: `dream/DamageEventDelta.cs`
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/Map.cs`
- Modify: `dream/PlayerCombatant.cs`

- [ ] Export accepted cube placement and destruction as explicit deltas
- [ ] Export damage and death outcomes as explicit deltas
- [ ] Keep local scene events and replication exports as separate layers
- [ ] Avoid sending full-world state for every mutation in the first PoC

### Task 4: Add initial snapshot export flow from the runtime

**Files:**
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/PlayScene.cs`
- Optional Create: `dream.Tests/WorldSessionSnapshotTests.cs`

- [ ] Give the runtime a clear API for producing current authoritative snapshot state
- [ ] Let the client path consume that API locally during transition, even before network transport exists
- [ ] Add focused tests only where pure export logic can be validated cleanly
- [ ] Keep snapshot generation deterministic enough for diagnostics and future server use

### Task 5: Verify and document

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`
- Create: `docs/ledger/work-log/2026-06-29-phase3-server-tick-and-snapshot-foundation.md`

- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Record whether the tick and snapshot path was verified only in-process or through a real server host
- [ ] Document any remaining transport gap honestly

## Self-Review Notes

- Spec coverage: this phase covers the 64 Hz tick contract, snapshot DTOs, mutation deltas, and initial export flow
- Placeholder scan: the plan stays explicit about file targets and phase responsibilities
- Type consistency: `ServerTickRunner`, `ActorSnapshot`, `WorldSnapshot`, and `ServerSnapshotEnvelope` remain stable throughout the phase
