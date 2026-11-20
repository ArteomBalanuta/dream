# Plan: the risen skeleton enemy, end to end

- Branch: `integrate/skeleton-enemy`, from master `16c17f01`.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery` — contracts frozen here
  first, then three implementers on disjoint files, then integration and measurement.

## What the inspection found

There is **no skeleton asset and no enemy-asset-generation skill on master**. The thing that exists,
and that this plan builds on, is `tools/ui-assets/gen_char_beast.py`: the forge that produced
`dream/animations/enemies/hound.glb`, "the first original SKINNED creature". The hound is the exact
precedent for this wave and almost every question this work raises is answered in its source.

What that precedent gives us:

- **Enemy kinds are data, not an enum.** There is no `enum EnemyKind`. A kind is a row in
  `dream/config/content/enemies.json` referenced by key, with the body (capsule, anchor, stand-off)
  shared from `WolfDefinition`. Four kinds ship: Wolf, DireWolf, AshenProwler, GraveHowler.
- **The visual is a second, independent table.** `EnemyVisualCatalog` maps a `visualKey` the server
  sends to a model path, a scale multiplier, a tint offset and an `EnemyClipSet`. The wire value is
  hostile: an unknown key draws the wolf. So a client that has not been updated degrades, it does not
  crash — and **no protocol bump is needed**, because the kind travels as the existing visual-key
  string, not as a new enumerant.
- **A forged model measures its own height.** `BeastAssetDefinition` resolves a
  `WorldAssetNormalization` from the GLB on first use, so re-forging cannot silently change the world
  height. A hard-coded authored height (the wolf's `3.191f`) is the third-party pattern, not ours.
- **Clips are bare names** — `idle`, `walk`, `gallop`, `attack`, `die` — not the wolf pack's
  `AnimalArmature|Idle`. `EnemyClipSet` requires five names, and a model with only one gait **names
  its Move clip as its Sprint**, which makes the gait choice a no-op.
- **`FindTemplate` returns the FIRST template of a role, in file order.** Appending a new kind at the
  END of `enemies.json` therefore cannot change which template the population step picks for either
  slot. That is the whole reason this wave can add a kind without touching the spawn logic.

## The frozen contract

Changing any of this means every workstream re-bases. Only the Lead may change it.

| Thing | Value |
| --- | --- |
| Enemy key | `RisenSkeleton` |
| Display name | `Risen Skeleton` |
| Visual key | `enemy.skeleton` |
| Model path | `dream/animations/enemies/skeleton.glb` |
| Generator | `tools/ui-assets/gen_char_skeleton.py` |
| C# asset definition | `dream/Rendering/Models/SkeletonAssetDefinition.cs` (new file) |
| Clip names (bare) | `idle`, `walk`, `attack`, `die` |
| Sprint clip | `walk` — one gait, so `EnemyClipSet.Sprint` aliases `Move` |
| Loot table | `risen-skeleton` |
| Migration | `dream/config/migrations/0018_risen_skeleton_kind.sql` |

`SkeletonAssetDefinition` exposes exactly these members, and WS-B may rely on nothing else:

```csharp
public static class SkeletonAssetDefinition
{
    public const string IdleClip = "idle";
    public const string MoveClip = "walk";
    public const string AttackClip = "attack";
    public const string DieClip = "die";
    public static string ModelPath { get; }        // AssetPaths.Animation("enemies/skeleton.glb")
    public static float TargetWorldHeight { get; } // a biped: the PLAYER's world height, not the wolf's 0.60
    public static WorldAssetNormalization Normalization { get; }
    public static float VisualScale { get; }       // Normalization.ResolveUniformScale(), measured once
    public static Vector3 AuthoredForwardAxis { get; }
}
```

The authored stat row, which WS-C writes verbatim and WS-A does not read:

```json
{ "key": "RisenSkeleton", "displayName": "Risen Skeleton", "level": 6,
  "strength": 30, "vitality": 48, "attackSpeed": 0.689655,
  "aggroRange": 13, "attackRange": 1.5, "moveSpeed": 3.6, "patrolSpeed": 1.25,
  "attackIntervalSeconds": 1.45, "visualKey": "enemy.skeleton",
  "spawnRole": "NightExtra", "lootTable": "risen-skeleton",
  "goldMin": 25, "goldMax": 60, "maxHealth": 520 }
```

**This row was corrected twice during the wave, and both corrections came from the track building
against it.** The first draft authored `attackSpeed` as 0.681818, which is not the attack interval's
reciprocal — `EnemyKindsMigrationTests:54` pins `attackSpeed == 1 / attackIntervalSeconds` to five
decimals, and all four shipped kinds satisfy it exactly. The second draft chased at 3.4 with a 1.55
interval, both of which sit on the WRONG SIDE of the level-5 Ashen Prowler, contradicting the rule
`EnemyTemplate.cs` states in the comment the same change was editing: the chase speeds rise with the
tier and the intervals fall with it. Every number above now interpolates monotonically between the
Prowler (5) and the Grave Howler (8): patrol 1.2 -> 1.25 -> 1.3, chase 3.5 -> 3.6 -> 3.8, interval
1.5 -> 1.45 -> 1.4, aggro 12 -> 13 -> 14, health 400 -> 520 -> 700, strength 24 -> 30 -> 40.

The lesson is not about arithmetic. A tier's numbers are a *sequence*, and a new tier inserted into
one is only correct if every column still reads monotonically — checking each number against its
own validity gate passes a row that is individually legal and collectively wrong.

It sits **last** in the array, which is what keeps `FindTemplate` returning DireWolf for the night
slot and Wolf for the day slot; that ordering is now pinned by its own test. `attackRange` is 1.5 rather than the family's 1.35 because this is
the first enemy that swings a blade instead of biting, and the reach is the one number that should
differ. Tier 6 sits between the Ashen Prowler (5) and the Grave Howler (8); the chase speed follows
the shipped rule — faster than the player's walk, slower than the player's sprint, rising with tier.

## Tracks

| Branch | Owns | Job |
| --- | --- | --- |
| `ws/skeleton-forge` | `tools/ui-assets/gen_char_skeleton.py`, `dream/animations/enemies/skeleton.glb`, `tools/ui-assets/tests/{check_actors,check_sources}.py`, `tools/ui-assets/build_all.py`, `dream/Rendering/Models/SkeletonAssetDefinition.cs` | forge the biped, author the four clips, gate it |
| `ws/skeleton-client` | `dream/Rendering/Actors/EnemyVisualCatalog.cs`, `dream/Rendering/Core/ModelAssets.cs`, `dream/Client/Scenes/PlayScene.Lifetime.cs`, `dream.Tests/Rendering/Actors/EnemyVisualCatalogTests.cs`, `dream.Tests/Rendering/Models/ForgedActorAssetTests.cs` | the visual entry, its clips, and the tests that pin them |
| `ws/skeleton-content` | `dream/config/content/{enemies,spawns,loot}.json`, `dream/config/migrations/0018_*.sql`, `dream/Simulation/Content/EnemyTemplate.cs`, `dream.Tests/Simulation/Content/ContentCatalogTests.cs`, `dream.Tests/Server/Persistence/Migrations/EnemyKindsMigrationTests.cs`, `dream.Tests/Simulation/Content/SpawnCampTests.cs` | the kind, its camps, its loot, its tuning row |

Disjoint by file. `SkeletonAssetDefinition.cs` is a NEW file owned by the forge track, so the client
track compiles against a contract that is frozen here rather than against a file two tracks edit.

## Known hazards, carried into every brief

- **Render colours are albedo.** Output is albedo times a gain of about 2.17 with no tonemap. Lit
  catalog and generator colours stay at or below 0.40 per channel. A bone white authored at 0.9 goes
  flat white on screen; bone reads as bone at about 0.33–0.38.
- **The figure is small.** An actor is about 28x101 px at the shipped zoom. Detail on a thin axis
  never reaches the screen — frequency, not amplitude, and broad up-facing plates are the only place
  a bright pixel can live under a camera looking down at 50 degrees.
- **The joint cap is 64 in the engine and 32 by contract C3.** Above 64 `LoadSkinning` silently
  returns null and the mesh draws in bind pose, which looks like a posing bug rather than a load
  failure.
- **Clip quaternions are converted numerically**, never authored by hand in glTF space:
  `R_gltf = SWAP . R_iso . SWAP`. `check_actors.py` proves the conversion against CPU skinning.
- **The root translation channel REPLACES the bind translation**, so it must be built from the
  GROUNDED skeleton. The hound was 1.04 units out on every frame of `walk` and nothing threw.
- **`die` runs once and the engine holds the last frame**, so the final pose is the corpse.
- **Never `git stash`** — one stack across all worktrees. Use a WIP commit.
- **One test suite at a time**, and reset the worktree onto `integrate/skeleton-enemy` before work.
- Build output stays inside the worktree (`/p:BaseOutputPath=artifacts/verify-bin/`).

## Acceptance

- `python -I tools/ui-assets/gen_char_skeleton.py` reproduces the shipped GLB byte for byte.
- `python -I tools/ui-assets/tests/check_actors.py` passes, with a live negative for every new gate.
- `dotnet build dream/dream.csproj`: 0 errors. `dream.exe --validate-content`: exit 0.
- Full suite green on the integration branch.
- A capture shows the skeleton standing, walking, swinging and dead, at a size and value that read
  against the hound at the same distance.

## Deliberately not done

- No new combat skill, no undead damage type, no resistance system.
- No second gait. One walk, aliased as the sprint, which `EnemyClipSet` explicitly sanctions: a model
  with only one gait names its Move clip as its Sprint, so the gait choice becomes a no-op. (An
  earlier draft of this line claimed the chase speed sits BELOW the gait threshold, which is simply
  wrong — 3.6 is well above the threshold of 2.0, so the latch does flip. What follows from one clip
  is that the flip renames nothing, and therefore never resets clip time. That turned out to matter:
  see the reference-speed defect in the work log.)
- No pose-layer contract: the blade is part of the mesh, as the hound's collar is. A skinned actor
  cannot carry node overrides, which resolve by renderable node name.

## What the scouting pass settled, after the tracks were briefed

Two read-only passes over the enemy path and the asset forge came back once the briefs were out,
and amended the ownership map above. The findings worth keeping:

- **The hound was never a new enemy KIND.** Commit `46c6738a` added a second enemy *model* and
  re-pointed the existing `GraveHowler` at it. It touched no enum, no migration, no protocol version
  and no spawn policy. So the hound diff is a complete map of the client half of this work and says
  nothing about the server half — which is why the content track was briefed from `enemies.json` and
  migration `0017` instead.
- **No protocol bump.** The kind travels as `visualKey`, a *nullable string* in
  `ActorSnapshotJsonConverter`, defaulted to `null` for the wolf so a wolf-only world stays byte
  identical to before the field existed. An unknown key resolves to the wolf. The current version is
  **22**, not the 21 this project's notes still say — bumped by the skill-effect-visuals wave.
- **The body is global, not per-kind.** `WolfDefinition` hard-wires collision radius 0.30, height
  0.60, anchor 0.75 and stand-off 1.20 for *every* enemy, and `EnemyFactory` has no per-kind branch
  at all. A kind that needed a different body would have to turn that constant set into per-template
  fields — the one real code change a "big" new enemy forces. **A skeleton is player-shaped and a
  wolf's capsule is a reasonable fit, so this wave deliberately does not open it.**
- **Spawn camps are test-pinned by exact count.** `SpawnCampTests` asserts 12 camps, 9 day / 3 night,
  21 day members, 28 total, *and* that camp level tiers rise with distance from the world spawn. Any
  camp added here must be placed further out than the level-5 Ashen Prowler camps.
- **The authored row is boxed in from five directions**: `attackRange` must exceed ~1.0556 or the
  enemy shoves instead of biting; `patrolSpeed <= moveSpeed`; `patrolSpeed <= 1.6` and
  `moveSpeed >= 2.0` so nothing lands in the client's gait deadband; chase must sit strictly between
  the player's cruise and sprint; and no tuning row may warn at boot. The shipped numbers (1.25 and
  3.6) clear all five with room.
- **A duplicate clip name THROWS on load** (`LoadAnimations` uses `Dictionary.Add`), while an
  *unknown* clip name does not — `AnimatedModelInstance` stops and holds the pose. A silent freeze
  mid-stride is the failure the strict clip-set gate exists to prevent.
- **`check_actors.py` polices size in BYTES, not triangles**, and a ceiling set at four times the
  asset's size fails too, on the grounds that it has stopped constraining anything.
- **`--validate-content` checks no model, clip or visual key** — only the content JSON and the
  formula probe. The real asset gate is `ForgedActorAssetTests` against the shipped GLB.
