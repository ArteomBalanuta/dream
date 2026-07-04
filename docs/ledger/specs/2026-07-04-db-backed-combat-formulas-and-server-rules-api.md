Purpose: Define the approved design for storing combat formulas as DB strings and evaluating them authoritatively on the server.
Status: Approved
Scope: Player combat formulas, formula persistence, expression evaluation, and the server-owned rules API boundary.

## Goals

- Move player combat formulas out of hardcoded C# constants and into SQLite rows.
- Let HP, damage, crit, and attack-speed cadence be rebalanced by updating DB formula strings.
- Keep the server authoritative for all gameplay-relevant formula evaluation.
- Introduce a clean server-to-DB API so simulation code does not talk SQL directly.

## Non-Goals

- free-form SQL execution for gameplay formulas
- client-side authoritative formula evaluation
- enemy/NPC/item economy formulas in this slice
- a generic scripting system for all gameplay rules

## Formula Storage

### Table

Add a dedicated `combat_formulas` table with one row per authoritative formula key.

Required fields:

- `formula_key`: stable primary key such as `player.max_health`
- `expression_text`: arithmetic expression string stored in the DB
- `description`: human-readable explanation of what the formula controls
- `enabled`: boolean-like flag controlling whether the row is active
- `updated_utc`: timestamp for audit/debug visibility

### Initial Formula Keys

- `player.min_damage`
- `player.max_damage`
- `player.max_health`
- `player.critical_damage`
- `player.critical_chance`
- `player.attack_interval_seconds`

### Initial Expressions

- `player.min_damage`: `5 + strength * 2`
- `player.max_damage`: `10 + strength * 4`
- `player.max_health`: `50 + vitality * 5`
- `player.critical_damage`: `max_damage * 1.5`
- `player.critical_chance`: `0.15`
- `player.attack_interval_seconds`: `1.0 / attack_speed`

## Server-Owned Formula Evaluation

### Safety Model

- The server evaluates formula strings through a small arithmetic expression engine owned by the codebase.
- Supported input is intentionally narrow:
  - numeric literals
  - `+`, `-`, `*`, `/`
  - parentheses
  - whitelisted variable names
- No method calls, no SQL fragments, no reflection, and no arbitrary code execution are allowed.

### Evaluation Context

Player formula evaluation uses a typed context with these inputs:

- `strength`
- `attack_speed`
- `vitality`
- `level`

Derived formulas may depend on earlier computed values:

- `min_damage`
- `max_damage`
- `max_health`

Evaluation order is fixed and owned by the server. Later formulas can reference values produced by earlier ones.

### Validation

- Missing required formula keys fail startup for authoritative server boot.
- Invalid syntax fails formula-set load.
- Unknown variable names fail formula-set load.
- Division by zero or non-finite results fail evaluation for that formula set.
- The server must not silently fall back to ad hoc hardcoded values when a configured formula set is invalid.

## Server <-> DB API

### Persistence Boundary

Add a dedicated repository seam for server-owned combat rules:

- `ICombatFormulaRepository`
- `SqliteCombatFormulaRepository`
- `CombatFormulaDefinition`

This boundary owns SQL and row materialization only.

### Runtime Boundary

Add a dedicated service seam for simulation-facing use:

- `CombatFormulaSet`
- `CombatFormulaService`
- `CombatFormulaContext`

Responsibilities:

- load enabled formulas from the repository
- validate presence and syntax of required formula keys
- compile/cache parsed expressions
- evaluate typed `PlayerCombatDerivedStats`

Simulation code should depend on the service, not on SQLite or SQL-shaped models.

## Runtime Integration

- `PlayerCombatStats` remains the base/effective stat carrier for strength, attack speed, and vitality.
- Hardcoded `Derive()` logic is replaced by authoritative service-backed derivation.
- `PlayerCombatant` receives a derivation dependency so health/cooldown logic uses DB-backed formulas.
- `WorldSessionRuntime` receives the combat-formula service through the existing server composition path.
- `ServerBootstrap` loads the schema, creates the formula repository/service, and passes it into authoritative runtime construction.

## Multiplayer Authority

- The server computes effective combat stats after tool modifiers are applied.
- The client remains presentation-only for derived combat behavior.
- Snapshot and action-result payloads may continue carrying base stats for authoritative reconciliation, while the server internally uses DB-backed derived stats for attack cadence and damage.

## Test Expectations

- SQLite schema bootstrap seeds the initial formula rows.
- Formula repository round-trips enabled formulas correctly.
- Expression evaluation supports literals, operator precedence, parentheses, and whitelisted variables.
- Invalid expressions and unknown variables are rejected.
- Authoritative runtime restores and recomputes derived stats using DB-backed formulas.
- Attack interval changes when the DB formula changes, without code changes.
