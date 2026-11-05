# Master green: the attribute-ceiling fallout, and one real bug inside it

`master` at `04a8fbfe` failed **41 test rows across 32 distinct methods** out of 7938. This wave takes it to **0**. Three workstreams built in parallel on disjoint files, merged WS1 -> WS3 -> WS2, plus one fix loop.

## What the 41 failures actually were

They were not rot, and not thirteen separate problems. They were **one cause with two faces**, and the commits that caused it said so at the time.

`a39702e6` ("Every new character starts at 1000 in each attribute") and `1f422512` ("Raise the attribute ceiling to 10000 and measure points from the start") moved the player attribute scale 100x. The message on `1f422512` states the fallout was knowingly deferred: "expectations tied to the old 1000 cap ... still have to be brought over, on top of the eleven integration tests already waiting". Nobody came back for it, and every wave since has had to measure around it.

- **32 failures, stat/point expectations.** Two mechanisms. `PlayerStatRules.Start*` no longer means 10, so nine sites that wrote `StartAgility` meaning "the sheet's default" were reading 1000 and getting Defense 250 instead of 2. And the ceiling moved, so a point test pinned to Attribute 10 now sits *below* the 1000 point origin, where `PointsSpent` reads 0 whatever is spent and the budget assertion is unmeasurable. The `TestStatBaseline.PinTo` workaround had itself become the bug.
- **9 failures, persistence/integration.** All one cause. At Strength 1000, `player.min_damage = 5 + strength*2` is 2005 against a 100 hp cube, so a new character one-shots it. Every harness here is written for a character needing many hits, so there are no further damage rolls - hence "Only 1 hits landed", "no hit after the first periodic checkpoint", and "two worlds with different seeds never diverged" (divergence requires a roll).

The proof for the second group was already in the repo: `PersistentWorldPlacedCubeDamageIntegrationTests` fails with its own trace `t10:0hp; fractured at 10`, while its deliberate control twin `EphemeralWorldPlacedCubeDamageIntegrationTests` **passes** - the only difference being that the ephemeral host has no profile repository, so the character keeps the unseeded sheet at Strength 10.

## The one real production bug

`StatRules.DefaultMaxUnused` was tied to `PlayerCombatStats.AttributeCeiling` (10000), and `MaxBase` has explicit cases only for Strength, Vitality and AttackSpeed - so the fallback arm also caught **`StatId.Armor`**. The comment enumerated "Agility and Energy among them" without noticing Armor was in that set too. With `player.damage_taken_factor = 100 / (100 + armor)` the armour cap became 10000, i.e. **0.99% damage taken** - near immunity from one tampered gear roll.

The cap is load-bearing: `ItemRollCodec.TryRange` permits Armor rolls up to 100 per modifier for either op, 8 modifiers per source across 12 slots. Two places still documented the designed range and master had silently broken agreement with both: `ServerBootstrap.ProbeFormulas` probes armour 1 to 1000 calling it "(the effective range)", and `PlayerCombatant.cs:26` says "effective range 0..1000".

Fix: `DefaultMaxUnused` back to `1000f`, with explicit `Agility`/`Energy` cases at `MaxAgility`/`MaxEnergy` so the gear headroom `1f422512` wanted is preserved. The test that caught it (`WaveIAdversarialTests`, `Assert.InRange(effective, 0, 1000)`) was **not** re-truthed to 10000 - that would be weakening an assert to accommodate a defect.

## What this wave deliberately does NOT fix

The scale moved 100x and **no coefficient consuming an attribute was rescaled**. At the shipping default:

| formula | at 10, as tuned | at 1000, as shipped |
| --- | --- | --- |
| `player.min_damage = 5 + strength*2` | 25 | **2005** |
| `player.max_health = 50 + vitality*5` | 100 | **5050** |
| `Defense = agility / 4` | 2 | **250** |
| `attackSpeed + 0.01*agility`, cap 5.0 | 2.1 | **saturated** (from agility 300) |

So a shipped character one-shots the 100 hp cubes, is effectively unkillable (`StatRules.MaxEnemyBase` is still **50**, with `enemy.attack_damage = strength + 2`), and has a permanently maxed attack speed - the attack-speed effect of Agility is dead. That is why the "attack speed rises after an Agility spend" assertion in `MasterMpGapTests` could not be kept: the rise is unobservable in the shipped state, so it became an exact-value pin plus a hard `MaxAttackSpeed` pin rather than being faked.

Restoring the armour cap closes only the **gear** half of the near-immunity path: `MitigationArmor = EffectiveArmor + Defense`, and `Defense` is bounded only by the 10000 ceiling, so nothing clamps the sum - a naked character at the default is already at 28.6% damage taken. Three comments were corrected to say this accurately rather than implying the cap fixed mitigation outright.

**This is a balance decision reserved for the user.** No clamp, formula or coefficient was changed for it. Nothing here obstructs a future rescale: every re-truthed expectation is written against symbols (`NewCharacterAttribute`, `Start*`, `PlayerCombatStats.Max*`), so moving the scale carries most of them automatically, and the three needing hand edits all fail loudly rather than silently.

## Fix loop 1: a test that passed while covering nothing

`PersistentScriptedServer` has two constructors; the bundle-taking one seeds a modest baseline, the host-taking overload cannot. WS3 reported `WaveT1ClockDeterminismTests` was "the only non-ephemeral user of the host overload". It was wrong. `StoredWorldEditsRestartTests` builds **two** servers through that overload with no seeding, so both joined at Strength 1000 and one-shot the cube their script means to damage over several hits. It **passed** - because it compares left against right symmetrically and never asserted hit counts. Its coverage of accumulating damage was vacuous.

All three workstreams' filters were green, so only the review caught it. Now seeded, with an assert on the cube's hit count. The first attempt at that assert (`Record.HitTicks.Count > 1`) also passed unseeded, because enemy hits count too - so it was replaced by the cube-hit count. Seeded: 3 hits (64 -> 7 -> 0), breaks at tick 186. Seeding removed: 1 hit, breaks at tick 130, assert fails.

An invariant comment was added where the harness seeds: **never construct it after a deliberate profile deletion** (`BuildingsOutliveBuilderTests` is safe only by accident of call-site shape - it builds a raw host instead).

## Verification

| | total | failed |
| --- | --- | --- |
| master `04a8fbfe` | 7938 | **41** |
| `integrate/master-green-v2` | 7938 | **0** |

`dream` 18 warnings / 0 errors and `dream.Tests` 83 warnings / 0 errors, byte-identical warning sets to master, measured with `-t:Rebuild` on both (an incremental build shows 89 for the tests project because the six warnings in `dream` re-emit; not a regression). `check_sources.py`: 0 problems, 428 names scanned.

The suite also went from **~2h30m to ~11m**. The ceiling raise had made two `StatSheetTests` grid loops 100x more expensive - the worst took 18m alone and had been crashing testhost. A new `Axis(min, max, stride)` helper restores the old cost with the **same** 1001 samples per axis over a 10x wider range, appending the maximum when the range is not a whole multiple of the stride, so the clamp edge stays covered. This is the biggest practical win of the wave: a 2.5-hour suite does not get run, and an 11-minute one does.

## Traps worth remembering

- **Do not measure a baseline by checking a commit's tree into a working worktree** (`git checkout COMMIT -- .`). It leaves the branch's *added* files in place, the build fails, and `--no-build` then silently re-runs the branch's own binary - handing back a "baseline" identical to your branch. It was caught only because the two durations matched to within a second. Use a separate detached worktree, and always confirm `0 Error(s)` before trusting `--no-build`.
- **`ws3/ceiling-pins`** looks like this fix from its title ("Repair test expectations left stale by the attribute ceiling raise") but touches only two files and none of the stat/armour families. It also sits on an unmerged inventory-drop wave carrying a contended protocol v19 to v20 bump. Read for shape only, never merged.
