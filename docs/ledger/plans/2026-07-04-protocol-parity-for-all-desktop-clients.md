# Protocol Parity For All Desktop Clients Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove normal desktop gameplay paths that bypass the multiplayer protocol, then prepare the integrated host for an in-memory protocol transport.

**Architecture:** Treat every human-controlled desktop player as a protocol client. The authoritative runtime remains server-owned; the desktop scene keeps only client presentation, replicated-state intake, and input capture. The first implementation keeps TCP loopback as the concrete integrated transport while removing the optional no-remote gameplay branch.

**Tech Stack:** C# / .NET 10, existing `RemoteClientSession`, `DedicatedServerHost`, `DedicatedServerService`, `WorldSessionRuntime`, OpenTK desktop shell, xUnit.

---

## File Structure Direction

- `dream/Client/Scenes/PlayScene.cs`
  - make protocol client session required for desktop gameplay
  - remove normal no-remote authority branches
  - keep local runtime as a replicated presentation mirror
- `dream/Application/Game.cs`
  - continue constructing `PlayScene` only after startup connection success
- `dream/Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`
  - keep secondary command-driven primary-use bridge until all command/action intake is fully unified
- `dream.Tests/Simulation/Runtime/RuntimeAuthoritativeSessionHostTests.cs`
  - keep regression coverage for secondary command-driven enemy combat and cube placement
- `docs/ledger/work-log/`
  - record protocol parity progress and remaining transport work

### Task 1: Make `PlayScene` require a protocol client

**Files:**
- Modify: `dream/Client/Scenes/PlayScene.cs`

- [x] Change `_remoteClient` from nullable to required.
- [x] Change the constructor signature from optional `RemoteClientSession? remoteClient = null` to required `RemoteClientSession remoteClient`.
- [x] Remove startup inventory capture from the no-remote branch; connected clients receive inventory from `JoinAcceptedMessage`.
- [x] Keep `Game` construction aligned with the existing startup connection handoff.

### Task 2: Remove no-remote gameplay authority branches

**Files:**
- Modify: `dream/Client/Scenes/PlayScene.cs`

- [x] Always call `WorldSessionRuntime.AdvanceAuthoritativeTick(...)` with `allowGameplaySimulation: false` in the desktop scene.
- [x] Always send command frames through `RemoteClientSession` when connected.
- [x] Always apply join, snapshot, and action messages through the remote-authority path.
- [x] Always send stats and inventory actions through `PlayerActionMessage`.

### Task 3: Preserve current secondary primary-use regression

**Files:**
- Modify: `dream/Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`
- Modify: `dream.Tests/Simulation/Runtime/RuntimeAuthoritativeSessionHostTests.cs`
- Create: `docs/ledger/work-log/2026-07-04-secondary-session-primary-use-combat-bridge.md`

- [x] Keep the new regression `AdvanceTick_SecondaryRemotePrimaryUseCombat_DamagesEnemy`.
- [x] Add/keep the building regression `AdvanceTick_SecondaryRemotePrimaryUseBuilding_PlacesCube`.
- [x] Keep the server-side synthesized secondary primary-use bridge using `WorldSessionRuntime.TryCreateCommandPrimaryUseActionMessage(...)`.
- [x] Verify the focused multiplayer runtime tests pass.

### Task 4: Record first-slice completion and next transport step

**Files:**
- Create: `docs/ledger/work-log/2026-07-04-protocol-parity-first-slice.md`
- Modify: `docs/architecture.md`

- [x] Document that normal desktop gameplay now requires a protocol client session.
- [x] Document that TCP loopback is still the concrete integrated transport.
- [x] Document that in-memory loopback transport is the next implementation step, not completed in this slice.

### Verification

- [x] Run `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RuntimeAuthoritativeSessionHostTests|FullyQualifiedName~WorldSessionRuntimeCombatEventQueueTests|FullyQualifiedName~AuthoritativeActionFlowTests|FullyQualifiedName~DedicatedServerHostTickTests|FullyQualifiedName~NetworkSessionCoordinatorTests|FullyQualifiedName~RemotePlayerCommandRelayTests|FullyQualifiedName~ClientSimulationCommandPolicyTests"`.
- [x] Run `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`.
- [ ] Optionally run `DREAM_AUTO_CONNECT=1` desktop startup profiling after the in-memory transport task lands.
