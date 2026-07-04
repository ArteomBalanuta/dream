# Server-Backed Player Experience And HUD Progression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist player experience, award authoritative XP from enemy kills, evaluate progression from DB formulas, and surface level/XP in the gameplay HUD.

**Architecture:** Extend the existing server-owned formula service with progression keys, persist XP on the player profile row, route enemy kill rewards through `WorldSessionRuntime`, and expose authoritative progression through `HudStatusSnapshot`.

**Tech Stack:** C#, .NET 10, xUnit, Microsoft.Data.Sqlite, existing Dream server/runtime architecture

---

## File Map

- Modify: `dream/config/player-profiles.init.sql`
- Modify: `dream/Server/Persistence/PersistedCreatureStats.cs`
- Modify: `dream/Server/Persistence/SqlitePlayerProfileRepository.cs`
- Modify: `dream/Server/Rules/CombatFormulaKeys.cs`
- Modify: `dream/Server/Rules/CombatFormulaSet.cs`
- Modify: `dream/Server/Rules/CombatFormulaService.cs`
- Modify: `dream/Simulation/Players/PlayerCombatant.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionPresentationState.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream/Rendering/Hud/HudStatusSnapshot.cs`
- Modify: `docs/server-persistence-ddl.md`
- Test: `dream.Tests/Server/Persistence/SqlitePersistenceRepositoryTests.cs`
- Test: `dream.Tests/Server/Rules/CombatFormulaServiceTests.cs`
- Test: `dream.Tests/Simulation/Runtime/RuntimeAuthoritativeSessionHostTests.cs`
- Test: `dream.Tests/Rendering/Hud/HudStatusSnapshotTests.cs`
- Log: `docs/ledger/work-log/2026-07-04-server-backed-player-experience-and-hud-progression.md`

### Task 1: Persistence And Formula Contract

- [ ] Add `experience` to player-profile persistence and schema docs
- [ ] Add progression formula keys and seeded rows

### Task 2: Red-Green For Persistence And Formula Evaluation

- [ ] Write failing tests for player-profile XP round-trip and progression formulas
- [ ] Run targeted tests and verify failures
- [ ] Implement persistence and formula-service support
- [ ] Run targeted tests and verify passes

### Task 3: Red-Green For Authoritative XP Rewards

- [ ] Write failing runtime tests for enemy-kill XP rewards and level-up behavior
- [ ] Run targeted tests and verify failures
- [ ] Implement authoritative XP grant, threshold checks, and persistence updates
- [ ] Run targeted tests and verify passes

### Task 4: Red-Green For HUD Progression

- [ ] Write failing HUD tests for level, percent, and raw progress labels
- [ ] Run targeted tests and verify failures
- [ ] Implement HUD snapshot fields and formatting
- [ ] Run targeted tests and verify passes

### Task 5: Final Verification And Work Log

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- [ ] Run `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
- [ ] Write the work-log entry
