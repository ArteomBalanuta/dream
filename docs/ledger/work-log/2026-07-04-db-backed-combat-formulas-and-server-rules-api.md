Purpose: Record the first pass that moved authoritative player combat formulas into SQLite-backed expression rows and introduced a server-owned rules API.
Status: Completed
Scope: Formula persistence, expression evaluation, runtime combat derivation wiring, and supporting documentation.

## Completed

- added a new `combat_formulas` SQLite table with seeded player combat expressions
- introduced a dedicated combat-formula persistence seam:
  - `ICombatFormulaRepository`
  - `SqliteCombatFormulaRepository`
  - `CombatFormulaDefinition`
- added a server-owned formula evaluation stack:
  - tokenization
  - strict arithmetic parsing
  - whitelisted variable validation
  - typed `CombatFormulaService` derivation
- rewired `PlayerCombatStats` and `PlayerCombatant` so authoritative derived stats can come from injected formula services
- threaded the combat-formula service through authoritative runtime/bootstrap composition
- preserved the existing starter-tool modifier flow so formulas evaluate after effective stats are assembled
- documented the new schema surface in `docs/server-persistence-ddl.md`

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~Dream.Tests.Server.Persistence.SqlitePersistenceRepositoryTests.SqliteCombatFormulaRepository_LoadAllEnabled_ReturnsSeededPlayerFormulas|FullyQualifiedName~Dream.Tests.Server.Rules.CombatFormulaServiceTests|FullyQualifiedName~Dream.Tests.Simulation.Players.PlayerCombatStatTests|FullyQualifiedName~Dream.Tests.Simulation.Runtime.RuntimeAuthoritativeSessionHostTests.JoinSession_UsesInjectedCombatFormulaServiceForDerivedHealthAndAttackCadence" --nologo /p:UseSharedCompilation=false`

## Notes

- This pass keeps formula expressions intentionally small and arithmetic-only.
- The current typed service is focused on player combat formulas; enemy and item formulas can extend the same seam later without reopening the SQLite boundary design.
