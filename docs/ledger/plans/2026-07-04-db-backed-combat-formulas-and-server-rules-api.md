# DB-Backed Combat Formulas And Server Rules API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move authoritative player combat formulas into SQLite string rows, expose them through a clean server-owned rules API, and wire runtime combat derivation to that API.

**Architecture:** Add a dedicated persistence seam for `combat_formulas`, a strict arithmetic-expression evaluator with whitelisted variables, and a simulation-facing `CombatFormulaService` that returns typed derived combat stats. The authoritative runtime will consume that service after tool modifiers are applied so DB updates change combat behavior without code edits.

**Tech Stack:** C#, .NET 10, xUnit, Microsoft.Data.Sqlite, existing Dream server/runtime architecture

---

## File Map

- Create: `dream/Server/Persistence/CombatFormulaDefinition.cs`
- Create: `dream/Server/Persistence/ICombatFormulaRepository.cs`
- Create: `dream/Server/Persistence/SqliteCombatFormulaRepository.cs`
- Create: `dream/Server/Rules/CombatFormulaContext.cs`
- Create: `dream/Server/Rules/CombatFormulaKeys.cs`
- Create: `dream/Server/Rules/CombatFormulaSet.cs`
- Create: `dream/Server/Rules/CombatFormulaService.cs`
- Create: `dream/Server/Rules/Expression/CombatExpressionCompiler.cs`
- Create: `dream/Server/Rules/Expression/CombatExpressionTokenizer.cs`
- Create: `dream/Server/Rules/Expression/CombatExpressionToken.cs`
- Modify: `dream/config/player-profiles.init.sql`
- Modify: `dream/Server/Persistence/ServerPersistenceBundle.cs`
- Modify: `dream/Server/ServerBootstrap.cs`
- Modify: `dream/Simulation/Players/PlayerCombatStats.cs`
- Modify: `dream/Simulation/Players/PlayerCombatant.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream/Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`
- Modify: `docs/server-persistence-ddl.md`
- Test: `dream.Tests/Server/Persistence/SqlitePersistenceRepositoryTests.cs`
- Test: `dream.Tests/Server/Rules/CombatFormulaServiceTests.cs`
- Test: `dream.Tests/Simulation/Players/PlayerCombatStatTests.cs`
- Test: `dream.Tests/Simulation/Runtime/RuntimeAuthoritativeSessionHostTests.cs`
- Log: `docs/ledger/work-log/2026-07-04-db-backed-combat-formulas-and-server-rules-api.md`

### Task 1: Ledger And Schema Contract

**Files:**
- Create: `docs/ledger/specs/2026-07-04-db-backed-combat-formulas-and-server-rules-api.md`
- Create: `docs/ledger/plans/2026-07-04-db-backed-combat-formulas-and-server-rules-api.md`
- Modify: `dream/config/player-profiles.init.sql`
- Modify: `docs/server-persistence-ddl.md`

- [ ] **Step 1: Add the `combat_formulas` DDL with seeded rows**
- [ ] **Step 2: Document the new table and its role in authoritative combat**
- [ ] **Step 3: Keep formula keys and descriptions aligned between SQL and docs**

### Task 2: Repository Red-Green For Formula Persistence

**Files:**
- Create: `dream/Server/Persistence/CombatFormulaDefinition.cs`
- Create: `dream/Server/Persistence/ICombatFormulaRepository.cs`
- Create: `dream/Server/Persistence/SqliteCombatFormulaRepository.cs`
- Test: `dream.Tests/Server/Persistence/SqlitePersistenceRepositoryTests.cs`

- [ ] **Step 1: Write a failing repository test for seeded formula load**
- [ ] **Step 2: Run the targeted persistence test and verify the new assertion fails**
- [ ] **Step 3: Add the formula persistence models and repository implementation**
- [ ] **Step 4: Run the targeted persistence test and verify it passes**

### Task 3: Expression Engine Red-Green

**Files:**
- Create: `dream/Server/Rules/Expression/CombatExpressionToken.cs`
- Create: `dream/Server/Rules/Expression/CombatExpressionTokenizer.cs`
- Create: `dream/Server/Rules/Expression/CombatExpressionCompiler.cs`
- Create: `dream/Server/Rules/CombatFormulaContext.cs`
- Create: `dream/Server/Rules/CombatFormulaKeys.cs`
- Create: `dream/Server/Rules/CombatFormulaSet.cs`
- Create: `dream/Server/Rules/CombatFormulaService.cs`
- Test: `dream.Tests/Server/Rules/CombatFormulaServiceTests.cs`

- [ ] **Step 1: Write failing tests for precedence, parentheses, variable lookup, and invalid expressions**
- [ ] **Step 2: Run the targeted rules tests and verify they fail for the expected missing behavior**
- [ ] **Step 3: Implement the minimal tokenizer, parser/compiler, and typed service**
- [ ] **Step 4: Run the targeted rules tests and verify they pass**

### Task 4: Authoritative Runtime Red-Green

**Files:**
- Modify: `dream/Simulation/Players/PlayerCombatStats.cs`
- Modify: `dream/Simulation/Players/PlayerCombatant.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream/Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`
- Modify: `dream/Server/Persistence/ServerPersistenceBundle.cs`
- Modify: `dream/Server/ServerBootstrap.cs`
- Test: `dream.Tests/Simulation/Players/PlayerCombatStatTests.cs`
- Test: `dream.Tests/Simulation/Runtime/RuntimeAuthoritativeSessionHostTests.cs`

- [ ] **Step 1: Write failing tests showing derived stats and attack cadence can change from DB-backed formulas**
- [ ] **Step 2: Run the targeted simulation tests and verify they fail**
- [ ] **Step 3: Inject the formula service into player combat derivation and authoritative runtime composition**
- [ ] **Step 4: Run the targeted simulation tests and verify they pass**

### Task 5: Final Verification And Work Log

**Files:**
- Create: `docs/ledger/work-log/2026-07-04-db-backed-combat-formulas-and-server-rules-api.md`

- [ ] **Step 1: Run targeted persistence, rules, and runtime tests**
- [ ] **Step 2: Run `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`**
- [ ] **Step 3: Write the work-log entry with what changed and what was verified**
