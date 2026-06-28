# Combat Feedback Decoupling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the direct gameplay-to-HUD floating-damage dependency by publishing combat domain events and reacting to them through a subscriber-owned feedback path.

**Architecture:** Use the new `SceneEventHub` to publish `DamageAppliedEvent` facts from gameplay-side code. Add a dedicated `CombatFeedbackSystem` subscriber that converts those events into HUD-owned floating combat text behavior without requiring `PlayScene` to call `HudRenderer.SpawnDamage(...)` directly.

**Tech Stack:** C# 13 / .NET 10, xUnit, existing `CombatActorHandle`, existing `FloatingCombatTextSystem`, existing `HudRenderer`

---

### Task 1: Define the first combat domain event with tests

**Files:**
- Create: `dream/DamageAppliedEvent.cs`
- Create: `dream.Tests/CombatFeedbackSystemTests.cs`
- Test: `dream.Tests/dream.Tests.csproj`

- [ ] **Step 1: Write the failing event-driven test**

```csharp
using OpenTK.Mathematics;
using Xunit;

public class CombatFeedbackSystemTests
{
    [Fact]
    public void Damage_Event_Spawns_Floating_Text_Through_Hud_Feedback_Path()
    {
        SceneEventHub hub = new();
        FloatingCombatTextSystem floatingText = new();
        CombatFeedbackSystem system = new(hub, floatingText);
        CombatActorHandle actor = new(
            Id: 7,
            Kind: CreatureKind.Player,
            Damageable: new HealthState(100),
            AnchorProvider: new FixedAnchorProvider(new Vector3(2f, 3f, 4f)));

        hub.Publish(new DamageAppliedEvent(actor, 24, FloatingCombatTextKind.Regular));

        List<FloatingCombatTextRenderState> states = new();
        floatingText.CollectRenderStates(states);

        Assert.Single(states);
        Assert.Equal("24", states[0].Text);
        Assert.Equal(new Vector3(2f, 3f, 4f), states[0].WorldAnchor);
    }

    private sealed class FixedAnchorProvider : ICombatAnchorProvider
    {
        private readonly Vector3 _anchor;

        public FixedAnchorProvider(Vector3 anchor)
        {
            _anchor = anchor;
        }

        public Vector3 GetCombatAnchorWorldPosition() => _anchor;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CombatFeedbackSystemTests" --nologo`

Expected: FAIL because `DamageAppliedEvent` and `CombatFeedbackSystem` do not exist yet.

- [ ] **Step 3: Add a negative-amount guard test**

Append this test to `dream.Tests/CombatFeedbackSystemTests.cs`:

```csharp
[Fact]
public void Non_Positive_Damage_Does_Not_Spawn_Floating_Text()
{
    SceneEventHub hub = new();
    FloatingCombatTextSystem floatingText = new();
    CombatFeedbackSystem system = new(hub, floatingText);
    CombatActorHandle actor = new(
        Id: 3,
        Kind: CreatureKind.Object,
        Damageable: new HealthState(50),
        AnchorProvider: new FixedAnchorProvider(Vector3.One));

    hub.Publish(new DamageAppliedEvent(actor, 0, FloatingCombatTextKind.Regular));

    List<FloatingCombatTextRenderState> states = new();
    floatingText.CollectRenderStates(states);

    Assert.Empty(states);
}
```

- [ ] **Step 4: Run tests to verify they fail for the missing types**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CombatFeedbackSystemTests" --nologo`

Expected: FAIL with missing-type errors only.

- [ ] **Step 5: Commit**

```bash
git add dream.Tests/CombatFeedbackSystemTests.cs
git commit -m "test: add event-driven combat feedback coverage"
```

### Task 2: Implement the event and subscriber-owned feedback system

**Files:**
- Create: `dream/DamageAppliedEvent.cs`
- Create: `dream/CombatFeedbackSystem.cs`
- Modify: `dream.Tests/CombatFeedbackSystemTests.cs`
- Test: `dream.Tests/CombatFeedbackSystemTests.cs`

- [ ] **Step 1: Add the event payload**

Create `dream/DamageAppliedEvent.cs`:

```csharp
public readonly record struct DamageAppliedEvent(
    CombatActorHandle Actor,
    int Amount,
    FloatingCombatTextKind Kind);
```

- [ ] **Step 2: Add the event subscriber**

Create `dream/CombatFeedbackSystem.cs`:

```csharp
public sealed class CombatFeedbackSystem : IDisposable
{
    private readonly FloatingCombatTextSystem _floatingText;
    private readonly IDisposable _damageSubscription;

    public CombatFeedbackSystem(SceneEventHub events, FloatingCombatTextSystem floatingText)
    {
        _floatingText = floatingText;
        _damageSubscription = events.Subscribe<DamageAppliedEvent>(OnDamageApplied);
    }

    public void Update(float deltaTime)
    {
        _floatingText.Update(deltaTime);
    }

    public void CollectRenderStates(List<FloatingCombatTextRenderState> destination)
    {
        _floatingText.CollectRenderStates(destination);
    }

    public void Dispose()
    {
        _damageSubscription.Dispose();
    }

    private void OnDamageApplied(DamageAppliedEvent evt)
    {
        if (evt.Amount <= 0)
        {
            return;
        }

        _floatingText.Spawn(evt.Actor.Id, evt.Actor.GetWorldAnchor(), evt.Amount, evt.Kind);
    }
}
```

- [ ] **Step 3: Run the combat feedback tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CombatFeedbackSystemTests" --nologo`

Expected: PASS.

- [ ] **Step 4: Add a disposal regression test**

Append this test to `dream.Tests/CombatFeedbackSystemTests.cs`:

```csharp
[Fact]
public void Disposed_System_No_Longer_Reacts_To_Damage_Events()
{
    SceneEventHub hub = new();
    FloatingCombatTextSystem floatingText = new();
    CombatFeedbackSystem system = new(hub, floatingText);
    CombatActorHandle actor = new(
        Id: 1,
        Kind: CreatureKind.Player,
        Damageable: new HealthState(100),
        AnchorProvider: new FixedAnchorProvider(Vector3.Zero));

    system.Dispose();
    hub.Publish(new DamageAppliedEvent(actor, 9, FloatingCombatTextKind.Regular));

    List<FloatingCombatTextRenderState> states = new();
    floatingText.CollectRenderStates(states);

    Assert.Empty(states);
}
```

- [ ] **Step 5: Run tests again**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CombatFeedbackSystemTests" --nologo`

Expected: PASS including the disposal regression.

### Task 3: Replace the direct HUD spawn call from `PlayScene`

**Files:**
- Modify: `dream/HudRenderer.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `docs/architecture.md`
- Test: `dream.Tests/CombatFeedbackSystemTests.cs`

- [ ] **Step 1: Simplify `HudRenderer` to expose feedback state rather than spawn entry points**

Update `dream/HudRenderer.cs` to accept a feedback source:

```csharp
public HudFrameResult DrawFrame(
    FrameEventArgs args,
    bool windowFocused,
    GameSettings settings,
    MouseState mouseState,
    Camera camera,
    CombatFeedbackSystem combatFeedback)
{
    return _render.DrawHUD(args, windowFocused, settings, mouseState, camera, combatFeedback);
}
```

Remove the public `SpawnDamage(...)` methods from `HudRenderer`.

- [ ] **Step 2: Thread the feedback system through `PlayScene`**

Update the `PlayScene` fields and constructor:

```csharp
private readonly SceneEventHub _events = new();
private readonly CombatFeedbackSystem _combatFeedback;

public PlayScene(GameSettings settings)
{
    _settings = settings;
    _physics = new PhysicsIntegrator();
    _map = new Map(_physics, width: _settings.FloorSize, depth: _settings.FloorSize);
    _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);

    Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
    _player = new Player(_map, _physics, _physics.PlayerBody, playerStartPosition);
    _render = new Render(_settings);
    _combatFeedback = new CombatFeedbackSystem(_events, new FloatingCombatTextSystem());
}
```

- [ ] **Step 3: Publish the damage event instead of calling the HUD directly**

Replace the direct call in `PlayScene.Update()`:

```csharp
if (primaryUseTriggered)
{
    _viewModelController.TriggerPrimaryUse();
    _events.Publish(new DamageAppliedEvent(
        _player.Combatant.Actor,
        Random.Shared.Next(120, 481),
        FloatingCombatTextKind.Regular));
}
```

Also replace the HUD update call:

```csharp
_combatFeedback.Update(delta);
```

And in `Render(...)` call:

```csharp
HudFrameResult hudResult = _render.Hud.DrawFrame(args, windowFocused, _settings, mouseState, _player.Camera, _combatFeedback);
```

- [ ] **Step 4: Update the architecture note**

Add or replace the floating-combat-text paragraph in `docs/architecture.md` so it reflects the new rule:

```md
The floating-combat-text path is now event-driven inside the scene boundary. Gameplay publishes `DamageAppliedEvent`, `CombatFeedbackSystem` subscribes and translates it into transient feedback state, and the HUD/render layer only draws the resulting presentation data.
```

- [ ] **Step 5: Run build and the full test suite**

Run: `dotnet build dream\\dream.csproj --nologo`

Expected: PASS with the existing BulletSharp warning only.

Run: `dotnet test dream.Tests\\dream.Tests.csproj --nologo`

Expected: PASS.

### Task 4: Record the completed decoupling slice

**Files:**
- Create: `docs/ledger/work-log/2026-06-28-combat-feedback-decoupling.md`

- [ ] **Step 1: Add the work-log entry**

```md
# Combat Feedback Decoupling

Purpose: record the first gameplay-to-presentation decoupling slice using scene-local events.
Status: completed
Scope: floating combat text moved behind `DamageAppliedEvent`

## Summary

Removed the direct `PlayScene` to `HudRenderer` floating-damage dependency by routing combat feedback through a local scene event subscription.

## Completed Work

- added `DamageAppliedEvent`
- added `CombatFeedbackSystem`
- removed direct HUD spawn calls from gameplay orchestration
- kept HUD/render responsible for drawing only

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
```

- [ ] **Step 2: Commit**

```bash
git add docs/ledger/work-log/2026-06-28-combat-feedback-decoupling.md
git commit -m "docs: log combat feedback decoupling"
```
