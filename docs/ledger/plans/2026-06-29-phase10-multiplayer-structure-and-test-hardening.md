# Phase 10 Multiplayer Structure And Test Hardening

Goal: convert the now-real multiplayer subsystem into a cleaner, more maintainable module layout while increasing regression coverage around the remaining session-ownership edge cases.

Architecture: perform bounded seam extraction first, then move multiplayer-specific files into grouped folders/namespaces, with tests added or updated in the same slices.

Tech Stack: C# / .NET 10, existing multiplayer runtime/client/server/protocol seams, xUnit

---

## Task 1: Consolidate session actor ownership lookup

**Files:**
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/PlayerSessionRegistry.cs`
- Modify: `dream/RemoteSessionPlayerState.cs`
- Create or modify: session-actor registry seam files as needed

- [ ] replace split primary-helper plus secondary-dictionary knowledge with one authoritative session actor lookup seam
- [ ] keep current join behavior honest while reducing duplication
- [ ] add focused tests for actor lookup, actor id stability, and controlled-session resolution

## Task 2: Add a controlled session actor facade for client-side correction

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/ClientCorrectionApplier.cs`
- Modify: `dream/ClientAuthoritativeActionApplier.cs`
- Create: controlled-actor facade files if needed

- [ ] stop hardcoding `runtime.Player` at every client correction/action application seam
- [ ] preserve current one-controlled-actor desktop behavior
- [ ] add edge-case tests for correction/action application scoped to the controlled session actor

## Task 3: Reorganize multiplayer files into grouped folders and namespaces

**Files:**
- Move: multiplayer-specific files under `dream/Multiplayer/*`
- Modify: affected namespaces/usings
- Modify: `docs/project-structure.md`
- Modify: `docs/architecture.md`

- [ ] move protocol/client/server/runtime/diagnostics files in bounded slices
- [ ] add matching namespaces only for moved multiplayer files first
- [ ] avoid unrelated gameplay/rendering file movement during this phase

## Task 4: Expand missing multiplayer edge-case coverage

**Files:**
- Modify: `dream.Tests/*multiplayer*.cs`
- Create: new targeted test files as needed
- Create: `docs/ledger/work-log/2026-06-29-phase10-multiplayer-structure-and-test-hardening.md`

- [ ] cover join alias semantics for the first remote session
- [ ] cover global versus per-actor interaction mode transition behavior
- [ ] cover multi-session enemy targeting and actor-correction edge cases
- [ ] document remaining non-goals honestly
