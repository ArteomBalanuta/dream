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
  "strength": 30, "vitality": 48, "attackSpeed": 0.681818,
  "aggroRange": 12, "attackRange": 1.5, "moveSpeed": 3.4, "patrolSpeed": 1.15,
  "attackIntervalSeconds": 1.55, "visualKey": "enemy.skeleton",
  "spawnRole": "NightExtra", "lootTable": "risen-skeleton",
  "goldMin": 25, "goldMax": 60, "maxHealth": 520 }
```

It sits **last** in the array, which is what keeps `FindTemplate` returning DireWolf for the night
slot and Wolf for the day slot. `attackRange` is 1.5 rather than the family's 1.35 because this is
the first enemy that swings a blade instead of biting, and the reach is the one number that should
differ. Tier 6 sits between the Ashen Prowler (5) and the Grave Howler (8); the chase speed follows
the shipped rule — faster than the player's walk, slower than the player's sprint, rising with tier.

## Tracks

| Branch | Owns | Job |
| --- | --- | --- |
| `ws/skeleton-forge` | `tools/ui-assets/gen_char_skeleton.py`, `dream/animations/enemies/skeleton.glb`, `tools/ui-assets/tests/check_actors.py`, `tools/ui-assets/build_all.py`, `dream/Rendering/Models/SkeletonAssetDefinition.cs` | forge the biped, author the four clips, gate it |
| `ws/skeleton-client` | `dream/Rendering/Actors/EnemyVisualCatalog.cs`, `dream.Tests/Rendering/Actors/EnemyVisualCatalogTests.cs`, `dream.Tests/Rendering/Models/ForgedActorAssetTests.cs` | the visual entry, its clips, and the tests that pin them |
| `ws/skeleton-content` | `dream/config/content/{enemies,spawns,loot}.json`, `dream/config/migrations/0018_*.sql`, `dream/Simulation/Content/EnemyTemplate.cs`, `dream.Tests/Simulation/Content/ContentCatalogTests.cs`, `dream.Tests/Server/Persistence/Migrations/EnemyKindsMigrationTests.cs` | the kind, its camps, its loot, its tuning row |

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
- No second gait. One walk, aliased as the sprint, because the authored chase speed (3.4) sits below
  the gait threshold and a `charge` clip would never play.
- No pose-layer contract: the blade is part of the mesh, as the hound's collar is. A skinned actor
  cannot carry node overrides, which resolve by renderable node name.
