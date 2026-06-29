# Phase 6 Authoritative Build Combat Validation And Correction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move building and combat fully behind server validation and add the first client correction path for rejected or conflicting local assumptions.

**Architecture:** Convert local primary-use behavior into explicit build/combat intents, resolve those intents on the authoritative runtime, emit result and correction payloads, and let the client confirm or rollback local assumptions without moving HUD or viewmodel presentation authority to the server.

**Tech Stack:** C# / .NET 10, authoritative runtime/session model, world mutation deltas, damage/result DTOs, client-local presentation systems

---

### Task 1: Define authoritative use-validation contracts

**Files:**
- Create: `dream/BuildUseIntent.cs`
- Create: `dream/CombatUseIntent.cs`
- Create: `dream/ValidatedUseRequest.cs`
- Create: `dream/AuthoritativeUseValidator.cs`
- Modify: `docs/architecture.md`

- [ ] Represent build and combat attempts as explicit intent contracts instead of implicit local side effects
- [ ] Make validation responsibilities visible and separate from world mutation execution
- [ ] Keep intent DTOs transport-safe and independent of local rendering state
- [ ] Avoid baking combat or build validation directly into `PlayScene`

### Task 2: Resolve validated actions into authoritative results

**Files:**
- Create: `dream/BuildUseResolver.cs`
- Create: `dream/CombatUseResolver.cs`
- Create: `dream/WorldMutationResult.cs`
- Create: `dream/CombatResolutionResult.cs`
- Modify: `dream/Map.cs`
- Modify: `dream/PlayerCombatant.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Resolve validated build actions into authoritative world mutation results
- [ ] Resolve validated combat actions into authoritative combat results
- [ ] Keep `Map` and combat state mutation behind authoritative results rather than direct local scene calls
- [ ] Leave room for future non-player combatants without assuming those files already exist

### Task 3: Export correction and action result payloads

**Files:**
- Create: `dream/AuthoritativeActionResult.cs`
- Create: `dream/ClientCorrectionDelta.cs`
- Create: `dream/RejectedUseDelta.cs`
- Create: `dream/ServerActionEnvelope.cs`
- Modify: `dream/ReplicationSnapshotBuilder.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Produce explicit outbound payloads for accepted action results and rejected use attempts
- [ ] Include enough data for the client to confirm or rollback local assumptions
- [ ] Keep replication export separate from local `SceneEventHub` publication
- [ ] Avoid sending full-world replacement state for every rejected or accepted use

### Task 4: Add client correction intake and local action history

**Files:**
- Create: `dream/ClientPredictionState.cs`
- Create: `dream/ClientCorrectionApplier.cs`
- Create: `dream/LocalActionHistory.cs`
- Create: `dream/RemoteWorldMutationApplier.cs`
- Modify: `dream/PlayerInteraction.cs`
- Modify: `dream/PlayScene.cs`

- [ ] Track local predicted actions only where responsiveness matters
- [ ] Apply authoritative confirmations or corrections cleanly on the client
- [ ] Keep correction logic separate from HUD, combat text, and viewmodel systems
- [ ] Prevent `PlayerInteraction` from remaining a direct local-authority placement path

### Task 5: Add diagnostics, tests, and ledger closeout

**Files:**
- Create: `dream/AuthoritativeFlowTrace.cs`
- Create: `dream/MultiplayerDiagnostics.cs`
- Create: `dream.Tests/AuthoritativeUseValidationTests.cs`
- Create: `dream.Tests/ClientCorrectionApplierTests.cs`
- Create: `dream.Tests/WorldMutationResultTests.cs`
- Create: `docs/ledger/work-log/2026-06-29-build-combat-validation-flow-planning.md`

- [ ] Add tests for accepted build, rejected build, accepted combat, rejected combat, and correction replay behavior
- [ ] Add diagnostics that tie session id, command sequence, and server tick to authoritative decisions
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo` and record limitations honestly

## Self-Review Notes

- Spec coverage: this phase covers validation contracts, authoritative action resolution, correction payloads, client rollback/confirmation, and diagnostics
- Placeholder scan: the plan uses concrete file names and bounded responsibilities throughout
- Type consistency: `AuthoritativeUseValidator`, `WorldMutationResult`, `CombatResolutionResult`, `ClientCorrectionDelta`, and `ClientCorrectionApplier` remain stable seams throughout
