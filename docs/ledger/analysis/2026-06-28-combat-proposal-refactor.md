# Combat Proposal Refactor Analysis

Date: 2026-06-28
Status: Analysis
Owner: Codex

## Purpose

Refactor the supplied combat-system proposal into shapes that fit the current `dream` project instead of the original Unity-oriented assumptions.

This analysis treats the user's existing [DMG_IMPL.md](G:\workspace\projects\dream\DMG_IMPL.md) as the strongest source for the floating-damage portion and treats the broader generated proposal as a source of ideas rather than a directly implementable architecture.

## Current Project Reality

The current project is:

- an OpenTK desktop game
- scene-driven through `Game -> PlayScene`
- renderer-layered through collaborators such as `WorldRenderer`, `ViewModelRenderer`, and `HudRenderer`
- physics-backed through Bullet
- currently centered on player movement, world rendering, HUD, and first-person presentation

It is **not**:

- a Unity project
- a `MonoBehaviour` / `Canvas` / `SerializeField` ecosystem
- a mature combat game with an established enemy framework

That means the proposal must be adapted before implementation.

## Key Findings

### 1. Floating Combat Text is the strongest and most ready slice

The floating-damage proposal already has:

- a clear visual identity
- explicit lifecycle timing
- performance constraints
- a project-relevant presentation problem

It fits the current repo well because the project already has:

- a HUD layer
- viewport-aware layout code
- runtime rendering ownership in focused collaborators

This is the best first implementation target.

### 2. The original creature hierarchy is the wrong shape for this repo

The generated proposal assumes a Unity-style inheritance tree:

- `Creature : MonoBehaviour`
- `PlayerEntity`
- `EnemyEntity`
- boss/minion subclasses

That is too engine-specific and too inheritance-heavy for this codebase.

The repo has already moved toward focused collaborators and smaller units. A better fit is:

- a small cross-cutting combat capability interface
- narrow data holders
- explicit scene-owned registration and update flow

In this repo, the goal should not be “force every combat-capable thing into a single deep inheritance tree.” The goal should be “let players, enemies, and damageable objects participate in combat through a shared capability contract.”

### 3. The stat matrix is promising but premature as a first landing

The stat proposal has useful long-term ideas:

- base values
- flat modifiers
- percent modifiers
- cached recalculation
- constraints like `MaxDamage >= MinDamage`

But the current repo does not yet show:

- a stable enemy loop
- item/equipment systems
- damage exchange architecture
- attack timing as a gameplay domain

So a full stat framework should not land before the combat target model and floating-damage feedback loop are proven.

### 4. The proposal should be decomposed into three ledgers

The combined proposal is too broad for a single implementation push.

Recommended decomposition:

1. `Floating combat text`
2. `Creature support foundation`
3. `Combat stats and modifiers`

That ordering matches risk and project maturity.

## Refactored Project-Fit Direction

### Track A: Floating Combat Text

Project-fit adaptation:

- use screen-space HUD rendering, not Unity world-space canvas logic
- add a focused HUD-side system for transient combat numbers
- compute screen positions from world anchor points
- use object pooling and bounded active-instance counts
- treat the design in `DMG_IMPL.md` as the canonical visual source

Likely repo fit:

- `FloatingCombatTextSystem`
- `FloatingCombatTextPool`
- `FloatingCombatTextStyle`
- `FloatingCombatTextRenderer`
- helper for world-anchor-to-screen conversion

### Track B: Creature Support Foundation

Project-fit adaptation:

- do not start with `Creature : MonoBehaviour`
- do start with a cross-system capability contract that lets different things receive combat events

Recommended shape:

- `IDamageable`
- `ICombatAnchorProvider`
- `CreatureKind` enum such as `Player`, `Enemy`, `Object`
- `CreatureHandle` or `CombatActorHandle` as a lightweight runtime identity
- `HealthComponent` or `VitalityState`

This supports:

- players
- future enemies
- breakable objects
- later combat feedback systems such as floating numbers and hit reactions

without locking the repo into a large inheritance tree too early.

### Track C: Combat Stats / Modifier Framework

Project-fit adaptation:

- keep it minimal until combat loops exist
- land only the stats required by the first attack/damage milestone
- prefer a compact stat container over a full RPG matrix at the start

The initial goal should be:

- damage intake
- current/max health
- maybe attack strength or attack interval if needed

Everything else can follow later.

## Recommended Sequencing

### Phase 1

Implement floating combat text using the existing HUD/render architecture.

Why first:

- immediate user-visible result
- lowest architectural risk
- validates world-to-screen transient feedback in this renderer

### Phase 2

Introduce creature support as a shared combat capability layer.

Why second:

- floating text wants a source for target anchor and damage events
- future enemies and objects need a shared contract

### Phase 3

Introduce a stat/modifier framework only after the first combat loop exists.

Why third:

- prevents overbuilding
- lets actual gameplay needs drive which stats are real

## Risks

### Risk: Importing Unity architecture verbatim

Consequence:

- engine mismatch
- awkward ownership boundaries
- wrong abstractions in core gameplay code

Mitigation:

- translate ideas into repo-native collaborators and contracts

### Risk: Building all combat systems at once

Consequence:

- large, fragile change set
- unclear testing path
- premature architecture lock-in

Mitigation:

- split into separate ledgers and plans

### Risk: Starting with the stat framework

Consequence:

- lots of code with little gameplay validation
- overgeneralized structures before combat targets exist

Mitigation:

- land stats after creature support and feedback systems

## Final Recommendation

Treat the original proposal as three distinct programs:

1. `Floating combat text` should move ahead first and follow the visual behavior in `DMG_IMPL.md`.
2. `Creature support` should be redesigned around interfaces and composition, not Unity inheritance.
3. `Combat stats` should be deferred until the first real combat interactions exist.

This gives the project a path toward combat support without distorting the current OpenTK/Bullet/render-layer architecture.
