# Player Combat Stats First Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add player-owned combat stats for strength, attack speed, and vitality, and use them to drive damage range, critical hits, cooldown-based combat cadence, HUD display, and runtime menu tuning.

**Architecture:** Keep combat math and cooldown state in focused player-combat types owned by `PlayerCombatant`, while leaving hit resolution and event publication in `PlayScene`. Extend the existing HUD snapshot/request seam so the menu can edit live player stats without turning `Render` or `GameSettings` into gameplay owners.

**Tech Stack:** C# / .NET 10, OpenTK, ImGui.NET, BulletSharp, xUnit, existing `PlayScene`, `PlayerCombatant`, `CombatFeedbackSystem`, `HudRenderer`, and `Render`

---

### Task 1: Add player combat stat models and derived-rule coverage

**Files:**
- Create: `dream/PlayerCombatStats.cs`
- Create: `dream/PlayerCombatDerivedStats.cs`
- Create: `dream/PlayerAttackResolution.cs`
- Create: `dream.Tests/PlayerCombatStatTests.cs`

- [ ] **Step 1: Write the failing combat-stat tests**

Create `dream.Tests/PlayerCombatStatTests.cs`:

```csharp
using Xunit;

public class PlayerCombatStatTests
{
    [Fact]
    public void Derive_Default_Stats_Returns_Expected_Combat_Profile()
    {
        PlayerCombatStats stats = new();

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(25, derived.MinDamage);
        Assert.Equal(50, derived.MaxDamage);
        Assert.Equal(75, derived.CriticalDamage);
        Assert.Equal(100, derived.MaxHealth);
        Assert.Equal(0.5f, derived.AttackIntervalSeconds, 3);
    }

    [Fact]
    public void Derive_Uses_Strength_To_Scale_Damage_Range()
    {
        PlayerCombatStats stats = new() { Strength = 12 };

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(29, derived.MinDamage);
        Assert.Equal(58, derived.MaxDamage);
    }

    [Fact]
    public void Derive_Uses_Vitality_To_Scale_Max_Health()
    {
        PlayerCombatStats stats = new() { Vitality = 16 };

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(130, derived.MaxHealth);
    }

    [Fact]
    public void Derive_Uses_Attack_Speed_To_Scale_Attack_Interval()
    {
        PlayerCombatStats stats = new() { AttackSpeed = 4f };

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(0.25f, derived.AttackIntervalSeconds, 3);
    }
}
```

- [ ] **Step 2: Run the focused tests to confirm missing types**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerCombatStatTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: FAIL with missing-type errors for the new player-combat stat types.

- [ ] **Step 3: Add the player stat holder and derived snapshot**

Create `dream/PlayerCombatStats.cs`:

```csharp
public sealed class PlayerCombatStats
{
    public const int MinStrength = 1;
    public const int MaxStrength = 50;
    public const float MinAttackSpeed = 0.5f;
    public const float MaxAttackSpeed = 5.0f;
    public const int MinVitality = 1;
    public const int MaxVitality = 50;

    public int Strength { get; set; } = 10;
    public float AttackSpeed { get; set; } = 2.0f;
    public int Vitality { get; set; } = 10;

    public PlayerCombatDerivedStats Derive()
    {
        int clampedStrength = Math.Clamp(Strength, MinStrength, MaxStrength);
        float clampedAttackSpeed = Math.Clamp(AttackSpeed, MinAttackSpeed, MaxAttackSpeed);
        int clampedVitality = Math.Clamp(Vitality, MinVitality, MaxVitality);

        int minDamage = 5 + (clampedStrength * 2);
        int maxDamage = 10 + (clampedStrength * 4);
        int maxHealth = 50 + (clampedVitality * 5);
        int criticalDamage = (int)MathF.Round(maxDamage * 1.5f);

        return new PlayerCombatDerivedStats(
            clampedStrength,
            clampedAttackSpeed,
            clampedVitality,
            minDamage,
            maxDamage,
            criticalDamage,
            maxHealth,
            0.15f,
            1.0f / clampedAttackSpeed);
    }
}
```

Create `dream/PlayerCombatDerivedStats.cs`:

```csharp
public readonly record struct PlayerCombatDerivedStats(
    int Strength,
    float AttackSpeed,
    int Vitality,
    int MinDamage,
    int MaxDamage,
    int CriticalDamage,
    int MaxHealth,
    float CriticalChance,
    float AttackIntervalSeconds);
```

Create `dream/PlayerAttackResolution.cs`:

```csharp
public readonly record struct PlayerAttackResolution(
    int Damage,
    bool IsCritical,
    FloatingCombatTextKind FloatingTextKind);
```

- [ ] **Step 4: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerCombatStatTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: PASS.

### Task 2: Teach `HealthState` and `PlayerCombatant` to support vitality-driven max HP and cooldown-based attacks

**Files:**
- Modify: `dream/HealthState.cs`
- Modify: `dream/PlayerCombatant.cs`
- Create: `dream.Tests/PlayerCombatantTests.cs`

- [ ] **Step 1: Write failing player-combatant tests**

Create `dream.Tests/PlayerCombatantTests.cs`:

```csharp
using OpenTK.Mathematics;
using Xunit;

public class PlayerCombatantTests
{
    [Fact]
    public void Constructor_Seeds_Default_Derived_Stats()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        Assert.Equal(100, combatant.Health.MaxHealth);
        Assert.Equal(25, combatant.DerivedStats.MinDamage);
        Assert.Equal(50, combatant.DerivedStats.MaxDamage);
    }

    [Fact]
    public void ApplyStats_Preserves_Health_Ratio_When_Max_Health_Changes()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);
        combatant.ApplyDamage(60);

        combatant.ApplyStats(new PlayerCombatStats { Strength = 10, AttackSpeed = 2f, Vitality = 20 });

        Assert.Equal(75, combatant.Health.CurrentHealth);
        Assert.Equal(150, combatant.Health.MaxHealth);
    }

    [Fact]
    public void TryStartAttack_Uses_Cooldown_Gating()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        Assert.True(combatant.TryStartAttack());
        Assert.False(combatant.TryStartAttack());

        combatant.AdvanceCombat(0.5f);

        Assert.True(combatant.TryStartAttack());
    }

    [Fact]
    public void ResolveAttack_Forces_Critical_When_Roll_Is_Within_Crit_Chance()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        PlayerAttackResolution attack = combatant.ResolveAttack(() => 25, () => 0.10f);

        Assert.True(attack.IsCritical);
        Assert.Equal(75, attack.Damage);
        Assert.Equal(FloatingCombatTextKind.Critical, attack.FloatingTextKind);
    }

    [Fact]
    public void ResolveAttack_Uses_Damage_Roll_For_Non_Crit()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        PlayerAttackResolution attack = combatant.ResolveAttack(() => 31, () => 0.90f);

        Assert.False(attack.IsCritical);
        Assert.Equal(31, attack.Damage);
        Assert.Equal(FloatingCombatTextKind.Regular, attack.FloatingTextKind);
    }
}
```

- [ ] **Step 2: Run the focused tests to confirm the missing behavior**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerCombatantTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: FAIL because `PlayerCombatant` does not yet expose derived stats, runtime stat application, cooldown gating, or attack resolution helpers.

- [ ] **Step 3: Add health resizing support**

Update `dream/HealthState.cs`:

```csharp
public void SetMaxHealthPreservingRatio(int maxHealth)
{
    int clampedMaxHealth = Math.Max(1, maxHealth);
    float ratio = MaxHealth <= 0 ? 1f : CurrentHealth / (float)MaxHealth;
    MaxHealth = clampedMaxHealth;
    CurrentHealth = Math.Clamp((int)MathF.Round(clampedMaxHealth * ratio), 0, clampedMaxHealth);
}
```

Also change the property declaration:

```csharp
public int MaxHealth { get; private set; }
```

- [ ] **Step 4: Move player combat rules into `PlayerCombatant`**

Update `dream/PlayerCombatant.cs`:

```csharp
public sealed class PlayerCombatant : IDamageable, ICombatAnchorProvider
{
    private readonly Func<Vector3> _positionProvider;
    private readonly float _anchorHeightOffset;
    private readonly SceneEventHub? _eventHub;
    private float _attackCooldownRemaining;

    public PlayerCombatStats Stats { get; }
    public PlayerCombatDerivedStats DerivedStats { get; private set; }
    public HealthState Health { get; }
    public CombatActorHandle Actor { get; }

    public PlayerCombatant(
        Func<Vector3> positionProvider,
        SceneEventHub? eventHub = null,
        int actorId = 1,
        float anchorHeightOffset = 1.2f,
        PlayerCombatStats? stats = null)
    {
        _positionProvider = positionProvider;
        _eventHub = eventHub;
        _anchorHeightOffset = anchorHeightOffset;
        Stats = stats ?? new PlayerCombatStats();
        DerivedStats = Stats.Derive();
        Health = new HealthState(DerivedStats.MaxHealth);
        Actor = new CombatActorHandle(actorId, CreatureKind.Player, Health, this);
    }

    public void AdvanceCombat(float deltaTime)
    {
        _attackCooldownRemaining = Math.Max(0f, _attackCooldownRemaining - Math.Max(0f, deltaTime));
    }

    public void ApplyStats(PlayerCombatStats updatedStats)
    {
        Stats.Strength = updatedStats.Strength;
        Stats.AttackSpeed = updatedStats.AttackSpeed;
        Stats.Vitality = updatedStats.Vitality;

        DerivedStats = Stats.Derive();
        Health.SetMaxHealthPreservingRatio(DerivedStats.MaxHealth);
    }

    public bool TryStartAttack()
    {
        if (_attackCooldownRemaining > 0f)
        {
            return false;
        }

        _attackCooldownRemaining = DerivedStats.AttackIntervalSeconds;
        return true;
    }

    public PlayerAttackResolution ResolveAttack(Func<int> damageRoll, Func<double> critRoll)
    {
        if (critRoll() <= DerivedStats.CriticalChance)
        {
            return new PlayerAttackResolution(
                DerivedStats.CriticalDamage,
                true,
                FloatingCombatTextKind.Critical);
        }

        int damage = Math.Clamp(damageRoll(), DerivedStats.MinDamage, DerivedStats.MaxDamage);
        return new PlayerAttackResolution(
            damage,
            false,
            FloatingCombatTextKind.Regular);
    }
}
```

- [ ] **Step 5: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerCombatantTests|FullyQualifiedName~PlayerCombatStatTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: PASS.

### Task 3: Route combat mode through the new player attack rules

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/FloatingCombatTextKind.cs`
- Modify: `dream/FloatingCombatTextStyles.cs`
- Create: `dream.Tests/PlaySceneCombatStatTests.cs`

- [ ] **Step 1: Add focused combat-resolution tests**

Create `dream.Tests/PlaySceneCombatStatTests.cs` with pure helper coverage extracted from the new player-combat path:

```csharp
using Xunit;

public class FloatingCombatTextKindTests
{
    [Fact]
    public void Critical_Style_Is_Red_And_High_Priority()
    {
        FloatingCombatTextStyle style = FloatingCombatTextStyles.Resolve(FloatingCombatTextKind.Critical);

        Assert.Equal(1.0f, style.Color.X, 3);
        Assert.Equal(0.15f, style.Color.Y, 3);
        Assert.Equal(0.15f, style.Color.Z, 3);
        Assert.True(style.LayerPriority < 10);
    }
}
```

- [ ] **Step 2: Run the focused tests to confirm the missing critical kind**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~FloatingCombatTextKindTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: FAIL because `FloatingCombatTextKind.Critical` does not exist yet.

- [ ] **Step 3: Update scene combat flow and crit feedback**

Update `dream/FloatingCombatTextKind.cs`:

```csharp
public enum FloatingCombatTextKind
{
    Regular,
    Critical,
    Incoming
}
```

Update `dream/FloatingCombatTextStyles.cs`:

```csharp
FloatingCombatTextKind.Critical => new FloatingCombatTextStyle(
    new Vector4(1.0f, 0.15f, 0.15f, 1.0f),
    1.75f,
    0),
```

Update `dream/PlayScene.cs` inside `Update(...)`:

```csharp
_player.Combatant.AdvanceCombat(delta);
```

before handling primary use, and replace the combat branch in `HandleCombatPrimaryUse()`:

```csharp
if (!_player.Combatant.TryStartAttack())
{
    return;
}

WorldObjectRecord? record = CombatUseResolver.TryResolveDestructibleCubeHit(
    _map,
    _worldObjects,
    _player.Camera.Position,
    _player.Camera.Front,
    CombatUseResolver.DefaultHitRange);

if (record == null)
{
    return;
}

PlayerAttackResolution attack = _player.Combatant.ResolveAttack(
    () => Random.Shared.Next(_player.Combatant.DerivedStats.MinDamage, _player.Combatant.DerivedStats.MaxDamage + 1),
    () => Random.Shared.NextDouble());

DamageReceipt receipt = record.ApplyDamage(attack.Damage);
```

and publish:

```csharp
_events.Publish(new DamageAppliedEvent(
    record.Actor,
    receipt.AppliedAmount,
    attack.FloatingTextKind));
```

- [ ] **Step 4: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerCombatantTests|FullyQualifiedName~FloatingCombatTextKindTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: PASS.

### Task 4: Extend HUD snapshot and request flow for combat stats

**Files:**
- Modify: `dream/HudStatusSnapshot.cs`
- Modify: `dream/HudInteractionRequest.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/PlayScene.cs`
- Create: `dream.Tests/HudStatusSnapshotTests.cs`

- [ ] **Step 1: Add failing HUD snapshot tests**

Create `dream.Tests/HudStatusSnapshotTests.cs`:

```csharp
using Xunit;

public class HudStatusSnapshotTests
{
    [Fact]
    public void Damage_Range_Label_Formats_Min_And_Max_Damage()
    {
        HudStatusSnapshot snapshot = new(
            100,
            100,
            PlayerInteractionMode.Combat,
            25,
            50,
            10,
            2.0f,
            10);

        Assert.Equal("Damage: 25-50", snapshot.DamageLabel);
    }
}
```

- [ ] **Step 2: Run the focused tests to confirm the missing HUD fields**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~HudStatusSnapshotTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: FAIL because the HUD snapshot does not yet carry combat-stat data.

- [ ] **Step 3: Add combat-stat status and menu requests**

Update `dream/HudStatusSnapshot.cs`:

```csharp
public readonly record struct HudStatusSnapshot(
    int CurrentHealth,
    int MaxHealth,
    PlayerInteractionMode InteractionMode,
    int MinDamage,
    int MaxDamage,
    int Strength,
    float AttackSpeed,
    int Vitality)
{
    public string HealthLabel => $"HP: {CurrentHealth}/{MaxHealth}";
    public string ModeLabel => $"Mode: {InteractionMode.ToHudLabel()}";
    public string DamageLabel => $"Damage: {MinDamage}-{MaxDamage}";
}
```

Update `dream/HudInteractionRequest.cs`:

```csharp
public readonly record struct HudInteractionRequest(
    float? FieldOfViewDegrees,
    float? MouseSensitivity,
    float? NoclipSpeed,
    Vector2i? RequestedResolution,
    int? StagedFloorSize,
    int? RequestedFloorSize,
    bool? ShowCollisionGeometry,
    int? Strength,
    float? AttackSpeed,
    int? Vitality)
```

and update `None` to pass three extra `null` values.

Update the HUD block in `dream/Render.cs`:

```csharp
ImGui.Text(status.HealthLabel);
ImGui.Text(status.ModeLabel);
ImGui.Text(status.DamageLabel);
```

Add settings sliders:

```csharp
int strength = status.Strength;
if (ImGui.SliderInt("Strength", ref strength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength))
{
    requestStrength = strength;
}

float attackSpeed = status.AttackSpeed;
if (ImGui.SliderFloat("Attack Speed", ref attackSpeed, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed))
{
    requestAttackSpeed = attackSpeed;
}

int vitality = status.Vitality;
if (ImGui.SliderInt("Vitality", ref vitality, PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality))
{
    requestVitality = vitality;
}
```

Update `PlayScene.Render(...)` to build the richer snapshot from `_player.Combatant.DerivedStats` and `_player.Combatant.Stats`.

Update `ApplyHudRequest(...)` in `PlayScene.cs`:

```csharp
if (request.Strength.HasValue || request.AttackSpeed.HasValue || request.Vitality.HasValue)
{
    PlayerCombatStats updatedStats = new()
    {
        Strength = request.Strength ?? _player.Combatant.Stats.Strength,
        AttackSpeed = request.AttackSpeed ?? _player.Combatant.Stats.AttackSpeed,
        Vitality = request.Vitality ?? _player.Combatant.Stats.Vitality
    };

    _player.Combatant.ApplyStats(updatedStats);
}
```

- [ ] **Step 4: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~HudStatusSnapshotTests|FullyQualifiedName~GameSettingsTests" --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: PASS.

### Task 5: Verify the slice and update project docs

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-28-player-combat-stats-first-pass.md`

- [ ] **Step 1: Update the user-facing docs**

Add to `README.md` under HUD/settings notes:

```md
- The gameplay HUD now shows player HP, active mode, and the current derived damage range.
- The settings menu can now tune player `Strength`, `Attack Speed`, and `Vitality` at runtime.
```

Add to `docs/architecture.md`:

```md
`PlayerCombatant` now owns a small player-only stat model plus derived attack rules, while `PlayScene` keeps target resolution and event publication scene-owned.
```

- [ ] **Step 2: Add the work-log entry**

Create `docs/ledger/work-log/2026-06-28-player-combat-stats-first-pass.md`:

```md
Purpose: Record the first player-owned combat stat slice and its HUD/runtime menu integration.
Status: Completed
Scope: Player combat stats, cooldown-based attack cadence, crit feedback, HUD range display, and runtime stat editing

# Summary

Replaced the ad hoc combat damage roll with player-owned strength, attack speed, and vitality stats. Combat mode now respects cooldown-based attack cadence, critical hits publish a red floating-text style, and the top-left HUD plus settings menu expose the current player combat tuning.

# Verification

- `dotnet build dream\\dream.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`
```

- [ ] **Step 3: Run full verification**

Run: `dotnet build dream\\dream.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: PASS with the existing BulletSharp warning only.

Run: `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`

Expected: PASS.
