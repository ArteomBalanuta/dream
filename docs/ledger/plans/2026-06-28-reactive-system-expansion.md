# Reactive System Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the scene-local event model into future gameplay systems such as creatures, breakables, audio, VFX, and debug instrumentation without overbuilding the engine.

**Architecture:** Build on the event foundation and first combat decoupling slice by adding a small set of additional event payloads and subscriber-oriented systems only when they support real gameplay. Keep publishers thin, observer systems focused, and feature growth driven by actual gameplay loops instead of a speculative framework.

**Tech Stack:** C# 13 / .NET 10, xUnit, existing creature/combat capability layer, existing scene-local event architecture

---

### Task 1: Add actor-death event support to the combat layer

**Files:**
- Create: `dream/ActorDiedEvent.cs`
- Modify: `dream/PlayerCombatant.cs`
- Create: `dream.Tests/CombatActorEventTests.cs`

- [ ] **Step 1: Write the failing death-event test**

```csharp
using OpenTK.Mathematics;
using Xunit;

public class CombatActorEventTests
{
    [Fact]
    public void Fatal_Damage_Publishes_Actor_Died_Event()
    {
        SceneEventHub hub = new();
        List<ActorDiedEvent> observed = new();
        hub.Subscribe<ActorDiedEvent>(evt => observed.Add(evt));
        PlayerCombatant combatant = new(() => Vector3.Zero);

        combatant.ApplyDamage(100);
        hub.Publish(new ActorDiedEvent(combatant.Actor));

        Assert.Single(observed);
        Assert.Equal(combatant.Actor.Id, observed[0].Actor.Id);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CombatActorEventTests" --nologo`

Expected: FAIL because `ActorDiedEvent` does not exist yet.

- [ ] **Step 3: Implement the event payload**

Create `dream/ActorDiedEvent.cs`:

```csharp
public readonly record struct ActorDiedEvent(CombatActorHandle Actor);
```

- [ ] **Step 4: Run the test again**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CombatActorEventTests" --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add dream/ActorDiedEvent.cs dream.Tests/CombatActorEventTests.cs
git commit -m "feat: add actor death domain event"
```

### Task 2: Add a minimal debug or telemetry subscriber example

**Files:**
- Create: `dream/EventTraceSystem.cs`
- Modify: `dream/PlayScene.cs`
- Create: `dream.Tests/EventTraceSystemTests.cs`

- [ ] **Step 1: Write the failing subscriber test**

```csharp
using Xunit;

public class EventTraceSystemTests
{
    private readonly record struct TestTraceEvent(string Name);

    [Fact]
    public void Trace_System_Records_Published_Event_Names()
    {
        SceneEventHub hub = new();
        EventTraceSystem trace = new(hub);

        hub.Publish(new TestTraceEvent("cube-placed"));

        Assert.Contains("TestTraceEvent", trace.Entries);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EventTraceSystemTests" --nologo`

Expected: FAIL because `EventTraceSystem` does not exist yet.

- [ ] **Step 3: Implement the trace subscriber**

Create `dream/EventTraceSystem.cs`:

```csharp
public sealed class EventTraceSystem
{
    public List<string> Entries { get; } = new();

    public EventTraceSystem(SceneEventHub hub)
    {
        hub.Subscribe<ActorDiedEvent>(_ => Entries.Add(nameof(ActorDiedEvent)));
        hub.Subscribe<DamageAppliedEvent>(_ => Entries.Add(nameof(DamageAppliedEvent)));
    }
}
```

Then update the test to publish one of the supported events instead of `TestTraceEvent`:

```csharp
hub.Publish(new DamageAppliedEvent(
    new CombatActorHandle(1, CreatureKind.Player, new HealthState(100), new FixedTraceAnchorProvider()),
    10,
    FloatingCombatTextKind.Regular));
```

Include this helper in the test file:

```csharp
private sealed class FixedTraceAnchorProvider : ICombatAnchorProvider
{
    public OpenTK.Mathematics.Vector3 GetCombatAnchorWorldPosition() => OpenTK.Mathematics.Vector3.Zero;
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EventTraceSystemTests" --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add dream/EventTraceSystem.cs dream.Tests/EventTraceSystemTests.cs
git commit -m "feat: add event trace subscriber"
```

### Task 3: Document adoption rules so the event model does not sprawl

**Files:**
- Modify: `docs/architecture.md`
- Create: `docs/ledger/decisions/2026-06-28-scene-events-not-global-bus.md`

- [ ] **Step 1: Add a short architecture note**

Append a compact rule section in `docs/architecture.md`:

```md
Scene-local events are intended for cross-system facts and reactions. Ordered simulation work such as movement, physics stepping, transform sync, and render pass order should remain direct.
```

- [ ] **Step 2: Record the durable decision**

Create `docs/ledger/decisions/2026-06-28-scene-events-not-global-bus.md`:

```md
# Scene Events Not Global Bus

Purpose: record the architectural rule for event scope in `dream`.
Status: accepted
Scope: event ownership and usage boundaries

## Decision

`dream` uses scene-scoped event hubs for cross-system reactions rather than a global engine-wide event bus.

## Rationale

- keeps control flow local
- preserves scene ownership boundaries
- avoids singleton-driven coupling
- supports testing and future multi-scene growth

## Consequences

- publishers should emit facts, not generic commands
- direct simulation sequencing remains explicit
- new reactive systems should subscribe inside scene scope
```

- [ ] **Step 3: Verify file presence**

Run: `Test-Path docs\\ledger\\decisions\\2026-06-28-scene-events-not-global-bus.md`

Expected: `True`

- [ ] **Step 4: Commit**

```bash
git add docs/architecture.md docs/ledger/decisions/2026-06-28-scene-events-not-global-bus.md
git commit -m "docs: record scene-local event decision"
```

### Task 4: Record the phase completion

**Files:**
- Create: `docs/ledger/work-log/2026-06-28-reactive-system-expansion.md`

- [ ] **Step 1: Add the work-log entry**

```md
# Reactive System Expansion

Purpose: record the first extension of scene-local events beyond the initial combat-feedback slice.
Status: completed
Scope: additional domain events, trace/debug subscriber, and event-scope decision

## Summary

Extended the scene-local event model into broader reactive patterns while preserving the rule that simulation sequencing stays explicit.

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
```

- [ ] **Step 2: Commit**

```bash
git add docs/ledger/work-log/2026-06-28-reactive-system-expansion.md
git commit -m "docs: log reactive system expansion"
```
