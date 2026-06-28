# Wolf Enemy First Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the first real enemy actor to `dream` using an animated wolf asset, simple chase behavior, player/enemy melee combat exchange, and movement-animation playback through the existing glTF pipeline.

**Architecture:** Keep `PlayScene` as scene coordinator only, and add focused enemy runtime types for behavior, combat participation, and visual clip state. Integrate the wolf through the existing `AnimatedModel` / `ModelAssets` path, but avoid cube-specific render shortcuts or a premature generic AI framework.

**Tech Stack:** C# / .NET 10, OpenTK, BulletSharp, SharpGLTF, ImGui.NET, xUnit, existing combat capability layer, existing animated-model render path

---

### Task 1: Add the wolf asset and a focused asset-resolution seam

**Files:**
- Create: `dream/animations/enemies/` asset files for the wolf model and textures
- Modify: `dream/ModelAssets.cs`
- Create: `dream/WolfAssetDefinition.cs`
- Test: `dream.Tests/AssetPathsTests.cs` or a dedicated wolf-asset test file

- [ ] **Step 1: Write a failing asset-resolution test**

Add a focused test such as:

```csharp
[Fact]
public void Wolf_Asset_Definition_Uses_Project_Runtime_Path()
{
    Assert.EndsWith(
        Path.Combine("animations", "enemies", "wolf.glb"),
        WolfAssetDefinition.ModelPath,
        StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the focused asset test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~Wolf_Asset_Definition" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the asset definition type does not exist yet.

- [ ] **Step 3: Add the wolf asset files and metadata seam**

Create a focused metadata file such as:

```csharp
public static class WolfAssetDefinition
{
    public static string ModelPath => AssetPaths.Animation("enemies/wolf.glb");
    public const string IdleClip = "Idle";
    public const string MoveClip = "Run";
}
```

Then extend `ModelAssets.cs` with:

```csharp
public static AnimatedModel Wolf =>
    Get(WolfAssetDefinition.ModelPath);
```

- [ ] **Step 4: Re-run the focused asset test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~Wolf_Asset_Definition" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 2: Add enemy combat and behavior runtime types

**Files:**
- Create: `dream/EnemyCombatant.cs`
- Create: `dream/EnemyActor.cs`
- Create: `dream/EnemyBehaviorState.cs`
- Create: `dream.Tests/EnemyCombatantTests.cs`
- Create: `dream.Tests/EnemyActorTests.cs`

- [ ] **Step 1: Write failing enemy combat/behavior tests**

Add focused tests for:

- enemy takes damage and dies
- enemy attack cooldown gates repeated damage
- enemy behavior switches between idle/chasing/attacking based on player distance

Example shape:

```csharp
[Fact]
public void Enemy_Combatant_Publishes_Fatal_State_When_Health_Reaches_Zero() { ... }

[Fact]
public void Enemy_Actor_Transitions_To_Chasing_When_Player_Is_In_Aggro_Range() { ... }

[Fact]
public void Enemy_Actor_Transitions_To_Attacking_When_Player_Is_In_Melee_Range() { ... }
```

- [ ] **Step 2: Run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemyCombatantTests|FullyQualifiedName~EnemyActorTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the enemy runtime types do not exist yet.

- [ ] **Step 3: Implement the enemy combat adapter**

Add `EnemyCombatant` as a focused combat participant using:

- `HealthState`
- `CombatActorHandle`
- `IDamageable`
- `ICombatAnchorProvider`

It should expose:

- enemy health
- world anchor
- outgoing melee damage
- attack cooldown behavior

- [ ] **Step 4: Implement the enemy behavior actor**

Add `EnemyActor` with:

- world position
- aggro range
- attack range
- move speed
- behavior update against current player position
- current `EnemyBehaviorState`

The first pass should use simple direct movement toward the player.

- [ ] **Step 5: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemyCombatantTests|FullyQualifiedName~EnemyActorTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 3: Add wolf visual-state and animation-clip selection

**Files:**
- Create: `dream/EnemyVisualState.cs`
- Create: `dream/WolfAnimationResolver.cs`
- Create: `dream.Tests/WolfAnimationResolverTests.cs`

- [ ] **Step 1: Write failing animation-selection tests**

Add tests such as:

```csharp
[Theory]
[InlineData(EnemyBehaviorState.Idle, WolfAssetDefinition.IdleClip)]
[InlineData(EnemyBehaviorState.Chasing, WolfAssetDefinition.MoveClip)]
public void ResolveClip_Maps_Behavior_State_To_Wolf_Clip(EnemyBehaviorState state, string expected) { ... }
```

- [ ] **Step 2: Run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WolfAnimationResolverTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the resolver does not exist yet.

- [ ] **Step 3: Implement the clip resolver**

Add a small resolver that maps:

- idle -> idle clip
- roaming/chasing -> move clip
- attacking -> move clip for now unless the asset ships a dedicated attack clip

- [ ] **Step 4: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WolfAnimationResolverTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 4: Integrate the wolf into scene update, combat exchange, and removal

**Files:**
- Modify: `dream/PlayScene.cs`
- Create: `dream/EnemyRegistry.cs` or a small scene-local enemy collection type
- Create: `dream.Tests/EnemySceneIntegrationTests.cs`

- [ ] **Step 1: Write failing scene-integration tests**

Cover:

- wolf exists at scene startup or through scene spawn
- player combat can damage the wolf
- wolf can damage the player when in attack range on cooldown
- dead wolf is removed or marked inactive for scene updates

- [ ] **Step 2: Run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemySceneIntegrationTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because scene integration is not wired yet.

- [ ] **Step 3: Integrate scene-owned enemy orchestration**

Update `PlayScene` to:

- spawn the wolf
- update enemy behavior each frame
- process enemy melee damage against the player
- allow the current player combat path to target the wolf before or alongside world objects
- remove dead enemies from active scene participation

Keep behavior/combat internals out of `PlayScene` as much as possible.

- [ ] **Step 4: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemySceneIntegrationTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 5: Add world rendering support for the wolf model

**Files:**
- Modify: `dream/WorldRenderer.cs`
- Modify: `dream/Render.cs`
- Create: `dream/EnemyRenderState.cs`
- Create: `dream.Tests/EnemyRenderStateTests.cs`

- [ ] **Step 1: Write failing render-state tests**

Add a pure render-state test that validates:

- world transform is built from wolf position and facing
- current behavior selects the correct clip name

- [ ] **Step 2: Run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemyRenderStateTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the render-state seam does not exist yet.

- [ ] **Step 3: Add enemy render participation**

Implement a focused render path that:

- resolves the wolf `AnimatedModel`
- updates or applies the active clip
- renders the wolf in world space

Do not force the wolf through `Cube` rendering or `Cube.Animation`.

- [ ] **Step 4: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemyRenderStateTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 6: Verify the slice and document it

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-28-wolf-enemy-first-pass.md`

- [ ] **Step 1: Update project docs**

Document:

- first enemy actor exists
- wolf asset is repo-owned under runtime animations
- scene now supports actor-vs-actor combat

- [ ] **Step 2: Add the work-log entry**

Create:

`docs/ledger/work-log/2026-06-28-wolf-enemy-first-pass.md`

with:

- purpose
- scope
- summary
- verification commands

- [ ] **Step 3: Run full verification**

Run:

- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

Expected:

- build PASS with the known BulletSharp warnings
- tests PASS
