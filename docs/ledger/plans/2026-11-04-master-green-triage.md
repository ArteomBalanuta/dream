# Master green: triage and fix plan for the 30 standing failures

Measured on `master` @ `736ca79`, clean tree: **39 failures across 30 distinct names**, 7,398 passing,
47 skipped, 7,484 total. Plus `check_manifest.py` reporting 41 problems. Every other asset gate is at
0.

**The verdict: 29 of the 30 names are stale premises, 1 is a real production defect, 0 are flakes, 0
are tests that never tested their claim.** So this is a test-truth wave, not a bug hunt — with two
exceptions that matter a great deal, below.

This plan is committed **before** the first implementer launches, because an untracked file does not
exist inside a worktree and that has already cost this project a verification round.

## Why these accumulated

Every wave has verified itself by *comparing its failing-name set to master's* rather than by getting
to green. That is sound practice against blame-shifting, and it is how this set calcified: three
deliberate behaviour changes landed, each updated some of its tests, and the remainder became
permanent background noise that nobody read.

**The exit criterion retires that habit: `Failed: 0, Passed: 7437, Skipped: 47, Total: 7484`.** The
total must not fall. No test may be deleted, skipped, or have a `[Fact]` removed to get there.

## The one real production defect

`UiBatchTests.UiNamespaceOutsideGlFolder_DoesNotReferenceOpenTkGraphics` is **correct, and production
violates it.** `dream/Rendering/Ui/Menu/MenuUiRenderer.cs:5` carries `using OpenTK.Graphics.OpenGL4;`
and makes raw `GL.` calls at lines 97, 175 and 182-190. Introduced by `3bff7fe` (the main menu and
front-end art, 2026-11-02), which added the file and never moved its GL into `Ui/Gl/`. Not
player-visible: it is the invariant that keeps the sprite UI testable off a GL context, and sibling
invariants in `FxPassContractTests.cs:346` and `AnimatedModelDataTests.cs:189` show the convention is
real. It has been broken for two days.

## The thing hiding behind a "stale" test — read this before re-pinning anything

**The enemy base-stat clamp was widened 20x and nobody noticed.**
`EnemyFactoryTests.ARestore_OfAHostileRow_ClampsItsStats_AndNeverThrows` feeds a hostile row
`strength = int.MaxValue` and asserts the clamp lands on **50**. It now lands on **1000**, because
`EnemyFactory.cs:127` clamps with `StatRules.ClampBase`, whose `MaxBase(Strength)` is
`PlayerCombatStats.MaxStrength` — raised 50 → 1000 by `3fc2a4f` (the character stats sheet). Enemies
and players now share one cap.

With `enemy.attack_damage = strength + 2` (`0009_enemy_formulas.sql:6`), a corrupt or tampered enemy
row's worst case went from a **52-damage bite to a 1002-damage bite** — a one-shot on any player
against a base max health of 100. The shipped templates top out at Strength 40 (Wolf 10, DireWolf 16,
AshenProwler 24, GraveHowler 40), so 50 was a deliberate ceiling with headroom.

**DECIDED: restore a dedicated enemy ceiling at 50.** Enemies sharing the player cap is a side effect
of a stats-sheet commit, not a design decision; the clamp exists precisely to defend against corrupt
or tampered rows, and 50 keeps the original headroom over the shipped maximum of 40. Re-pinning the
test to 1000 would have accepted a robustness regression as a documentation update. This is a
production change in `StatRules.cs` / `EnemyFactory.cs` and touches `EnemyFactoryTests` only.

## The live gameplay change the stale tests were hiding — intentional, keep it

`3fc2a4f` folded `Defense = Agility / 4` into mitigation, so **every character now mitigates ~2% even
naked**: a 100-damage hit on a fresh 100 HP player applies 98 and no longer kills; a 30 hit applies
29. Agility also adds `+0.01` attack speed per point, so the default 2.0 became 2.1 and the attack
cadence moved 33 → 32 ticks.

**This is the design, not a bug.** `CharacterSheetModel.cs:43` already shows the player
`Derived("Defense", Int(s.Defense + s.Armor))`, so production and the HUD agree that mitigation armor
is gear Armor plus Defense. Only the vocabulary is wrong: `DamageTakenFactor`'s comment still claims
"1.0 at Armor 0", which it never is now. **Fix the comment and name the sum. Do not change the
arithmetic.**

## The potion is not broken

All three `ConsumableLoopbackTests` failures are in *setup arithmetic*, before any potion is used:
`Assert.Equal(2, potions.Length)` sees 3, and two `.Single()` calls throw. Cause: `80483b0` (starter
kit v4) put a `potion.health` and a `potion.mana` in every new character's bag at slots 24 and 25, and
the tests count *all* bag rows of that kind rather than the ones they bought. The heal-50 /
320-tick-cooldown / refuse-inside-cooldown / refuse-after-failed-write logic in
`WorldSessionRuntime.Consumables.cs:24-70` is intact, matches the content, and the one test in that
class which does not touch bags passes.

The same commit breaks all three `InventoryHostileIntentTests`: their fixture moves an item to bag
slot 24, which the starter potion now occupies, so the legitimate control action is *correctly*
rejected.

## Triage, by root cause

### Kind A — stale, from `3fc2a4f` (character stats sheet). 20 names / 29 failures.

That commit made three changes and updated only some tests: **(i)** `AttackSpeed` gains `+0.01` per
Agility point, default 2.0 → 2.1; **(ii)** `Defense = Agility/4` enters mitigation; **(iii)**
`MaxStrength`/`MaxVitality` 50 → 1000.

| Test | Observed | Cause |
| --- | --- | --- |
| `ArmorMitigationTests.Armor0_ApplyDamage_EqualsTheRawHit_ForEveryAmount1To1000` | 26 → 25 applied | ii |
| `ArmorMitigationTests.Armor_MitigatesWolfAndDireBites_AsTheDesignTable` | 10→9, 9→8 | ii |
| `ArmorMitigationTests.TheFactor_IsEvaluatedOnArmorChangeOnly_NotOnApplyBaseStats` | 100/140 → 100/142 | ii |
| `ArmorMitigationTests.EnemyActorTryAttack_AppliesTheMitigatedDamage` | 90 → 91 | ii |
| `WaveIAdversarialTests.ExtremeArmorModifiers_...` (7 cases) | `effective==0` no longer implies unmitigated | ii |
| `WaveIAdversarialTests.AnInfiniteSumTimesAZeroPercent_IsNaN_...` | factor != 1.0 | ii |
| `PlayerCombatantCombatStatTests.ApplyEquipment_Adjusts_EffectiveStats_...` | 4 → 4.1 | i |
| `PlayerCombatantCombatStatTests.ApplyEquipment_SumsAllFourSlots_AndReadsArmor` | 3 → 3.1 | i |
| `PlayerCombatantCombatStatTests.ApplyStats_Preserves_Health_Ratio_...` | 60 → 62 | ii |
| `EquipmentStatCombinationTests.EveryFourSlotCombination_ResolvesToTheOracle_...` | oracle 0.5 vs 0.6 | i (oracle has no Agility term) |
| `PlayerCombatantTests.Creates_Player_Handle_With_Head_Level_Anchor_And_Health` | 70 → 71 | ii |
| `CombatActorEventTests.Fatal_Damage_Publishes_Actor_Died_Event` | 100 applies 98, not fatal | ii |
| `RuntimeAuthoritativeSessionHostTests.ProcessAction_InventoryEquip_Recomputes_...` | 4 → 4.1 | i |
| `RuntimeAuthoritativeSessionHostTests.JoinSession_UsesInjectedCombatFormulaService...` | 0.889 → 0.851 s | i |
| `RuntimeAuthoritativeSessionHostTests.JoinSession_RestoresPersistedProfile...` | 3.75 → 3.85 | i |
| `AuthoritativeActionFlowTests.ProcessAuthoritativeAction_AppliesPlayerStatsUpdate...` | 4.5 → 4.6 | i |
| `AuthoritativeActionFlowTests.ProcessAuthoritativeAction_EquippedFastTool_...` | 4 → 4.1 | i |
| `Wave7HeldUseAndStatsValidationTests.StatsUpdate_InsideTheEditorRules_IsApplied` (3) | snapshot Agility/Energy 0 → 10 | widened snapshot |
| `AuthoritativeCombatResolverTests.RuntimePath_SeededRolls_MatchPreExtractionGolden...` | golden (50,Regular,33) → (44,Regular,32) | i: cadence 33 → 32 re-rolls every seeded hit |
| `EnemyFactoryTests.ARestore_OfAHostileRow_ClampsItsStats_AndNeverThrows` | 50 → 1000 | iii — **see the decision above; production is being fixed, not the test** |

### Kind A — stale, from the content and starter-kit commits. 8 names / 8 failures.

- `80483b0` (starter kit v4): `ConsumableLoopbackTests` x3, `InventoryHostileIntentTests` x3.
- `823f6eb` (the kit wears its torch): `HeldLightSnapshotBytesTests` x2.
  `NoLight_200Ticks_Is58132Bytes` measures **58,180** (+48 B) because every character now wears a lit
  torch, so the `heldLight` key is in every frame — **the test's name and premise are both obsolete,
  there is no unlit baseline any more**. `OneTorch_GrowthPerFrame` cannot find the torch in a bag row
  because it is worn; its control must become "unequip the worn torch".

### Kind D — a stale reference document, not a test: `check_manifest.py`'s 41 problems.

The gate compares the Manifest tables of `07-asset-plan.md` against the committed atlas. All 182
planned sprites exist at the right size; the 41 "extras" are the real item icons added by `1fd499c`
plus the two potions. `check_items.py` independently reports 69 sprites, 0 errors — the atlas is
healthy and the plan doc is two weeks behind the art. The gate also carries a hand count
`("items", 28)` that should be 69.

### Kind C — not a flake, an environment-fragile premise.

`ClientReplayReconciliationTests.SnapAfterStall_ReplaysUnacknowledgedTicks_AndLandsWhereTheServerGoes`
produced **byte-identical output across three isolated runs**, so it is deterministic, not timing or
port sensitive:

```
error at ack 4.454 m; snap landing (4.9550204, 1.5, 7), server after 6 more ticks (4.4881396, 1.5, 7), miss 0.4669 m
```

Both end at `z = 7` exactly while the walker drifts 0.47 m laterally in x — it is pressed against
generated terrain and sliding along it, so the test measures a collision slide, not the reconciler.
Its own comment admits the premise ("the startup terrain blocks some directions; walk along the most
open axis") and `ProbeMostOpenFacing` only maximises total displacement without requiring a straight
lane. Untouched since `797b631` while the world line moved under it several times.

**There is a secondary finding here worth a deliberate look: delta-replay snapping does overshoot
when the server's path is obstructed.** The bisect decides whether that is a test premise or a
production defect.

## Decisions taken, so nobody is blocked

- **Q1 — restore a dedicated enemy stat ceiling at 50.** Reasoning above. Production change.
- **Q2 — `EffectiveArmor` stays gear-only; add `MitigationArmor => EffectiveArmor + Defense`.** It
  matches `CharacterSheetModel.cs:43`, which already presents `Defense + Armor` as one HUD number.
  Making `EffectiveArmor` itself include Defense would be a semantic change with far wider reach.
- **Q3 — bisect first, then strengthen the lane.** If the bisect lands on a world or terrain change,
  require a *straight* lane in `ProbeMostOpenFacing` (lateral drift under ~1 cm over the probe) and
  fail loudly when no lane exists. Keep the test on real terrain for now; a synthetic flat map is the
  fallback if it proves fragile again. **If the bisect lands on the reconciler or on player movement,
  stop and report a real defect — do not edit the test.** The 0.05 m tolerance does not move under any
  circumstances.
- **Q4 — yes, rename the obsolete test.** `NoLight_200Ticks_Is58132Bytes` describes a baseline that no
  longer exists.
- **Q5 — scheduled as its own wave, not this one.** No can't-fail gate was found among the 30 failing
  names, but the ~7,400 passing tests and the other asset gates have not been audited for the
  `bind_span = stand; if bind_span <= 0.45 * stand` pattern that was caught in the models wave. That
  audit is worth doing and is explicitly out of scope here.

## Workstreams

Three, with disjoint file ownership. The split is by root cause, which is why the parallelism is real
rather than invented.

**WS-A — Stats premise** (20 names, 29 failures). Owns the combat and stats test files listed in the
Kind A table, plus `dream/Simulation/Players/PlayerCombatant.cs`, and
`dream/Simulation/Stats/StatRules.cs` + `dream/Simulation/Enemies/EnemyFactory.cs` for the Q1 cap.

**WS-B — Content premise** (8 names). Owns `ConsumableLoopbackTests.cs`,
`HeldLightSnapshotBytesTests.cs`, `InventoryHostileIntentTests.cs`.

**WS-C — Boundary, manifest and replay** (2 names + the 41 gate problems). Owns
`MenuUiRenderer.cs`, a new `dream/Rendering/Ui/Gl/UiGlFontTexture.cs`, `07-asset-plan.md`,
`check_manifest.py`'s hand count, `ClientReplayReconciliationTests.cs`, and
`ClientPredictionReconciler.cs` only if the bisect proves a defect there.

### Shared contracts

1. **No wire, protocol, migration or content-JSON change.** `dream/config/content/*.json`,
   `dream/config/migrations/*` and everything under `dream/Multiplayer/Protocol` are off-limits to all
   three. The content digest and the migration tests pass today and stay byte-identical.
2. WS-A's only production API addition is `PlayerCombatant.MitigationArmor`. Nobody else consumes it.
3. **Golden ownership: exactly two goldens may move** — `HeldLightSnapshotBytesTests`'s byte count
   (WS-B) and `AuthoritativeCombatResolverTests`'s seeded array (WS-A). **Any other golden or digest
   that moves is a regression and must be reported, not re-pinned.**
4. Merge order **WS-C → WS-A → WS-B**: the boundary fix is independent and may need a manual look, so
   it surfaces first; WS-A owns the only shared-risk production file, so it lands before anything
   measuring bytes derived from stats; WS-B's golden is measured on the integration branch *after*
   WS-A is in, and if it differs from 58,180 that is a signal WS-A changed behaviour it should not
   have.

## Where the pressure to cheat is concentrated

Six assertions have an easy wrong fix. Named here so a reviewer can go straight to them:

- `ConsumableLoopbackTests`' `Assert.Equal(2, ...)` and its three `.Single()` calls — exclude the
  starter rows by instance id, do **not** relax to `>=` or `.First()`.
- `WaveIAdversarialTests`' `effective == 0 => mitigated == amount` — restate against
  `MitigationArmor`, keep the `InRange` and fatal-hit assertions.
- `HeldLightSnapshotBytesTests`' `grownFrames i % 64 == 0` — that is the real content of the test.
- `ClientReplayReconciliationTests`' `miss < 0.05f` — does not move.
- `check_manifest.py`'s extra-sprite loop — no ignore list, no `continue`, no pattern exclusion.
  Adding the 41 sprites to the doc makes the gate *stronger*, since it size-checks them too.
- `EquipmentStatCombinationTests`' bit-exact attack-speed comparison — add the Agility term to the
  oracle as independent arithmetic from the constants, **not** by calling
  `PlayerStatRules.EffectiveAttackSpeed`, which would make it tautological.

And one structural guard: **every test that zeroes Agility to restore a pure-armor table must be
paired with a default-Agility case asserting a different number**, so the Defense term cannot silently
become dead code.

## Also carried into this wave's record

- Re-pinning `AuthoritativeCombatResolverTests`' array destroys a pre-refactor safety net that cannot
  be preserved, because the cadence genuinely changed. The live `ResolveSeededAttack` cross-check
  becomes the only guard. Say so in the hand-off, so nobody later believes the golden still proves the
  extraction was faithful.
- WS-C's menu change touches render-thread lifetime (the font texture is deleted in `TryCreate`'s
  catch path and in `Dispose`). A mistake leaks a texture or crashes the title screen and no unit test
  covers it, so it needs one manual front-end check.
