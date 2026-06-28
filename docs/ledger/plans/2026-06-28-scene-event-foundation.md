# Scene Event Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a small typed scene-local event hub that `PlayScene` can own and other gameplay systems can use without introducing a global bus.

**Architecture:** Introduce a compact `SceneEventHub` with typed publish/subscribe behavior and explicit subscription lifetime. Keep the hub scene-scoped, easy to test, and intentionally free of reflection, string keys, and singleton ownership. Integrate it first without changing gameplay behavior so later phases can decouple real systems safely.

**Tech Stack:** C# 13 / .NET 10, xUnit, existing `PlayScene` orchestration, existing ledger/spec workflow

---

### Task 1: Add focused tests for typed publish/subscribe behavior

**Files:**
- Create: `dream.Tests/SceneEventHubTests.cs`
- Test: `dream.Tests/dream.Tests.csproj`

- [ ] **Step 1: Write the failing test**

```csharp
using Xunit;

public class SceneEventHubTests
{
    private readonly record struct TestSceneEvent(int Value);

    [Fact]
    public void Publish_Delivers_Event_To_Typed_Subscriber()
    {
        SceneEventHub hub = new();
        int observed = 0;

        hub.Subscribe<TestSceneEvent>(evt => observed = evt.Value);

        hub.Publish(new TestSceneEvent(7));

        Assert.Equal(7, observed);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~SceneEventHubTests.Publish_Delivers_Event_To_Typed_Subscriber" --nologo`

Expected: FAIL because `SceneEventHub` does not exist yet.

- [ ] **Step 3: Expand the test file with unsubscribe and multi-subscriber coverage**

```csharp
using Xunit;

public class SceneEventHubTests
{
    private readonly record struct TestSceneEvent(int Value);

    [Fact]
    public void Publish_Delivers_Event_To_Typed_Subscriber()
    {
        SceneEventHub hub = new();
        int observed = 0;

        hub.Subscribe<TestSceneEvent>(evt => observed = evt.Value);

        hub.Publish(new TestSceneEvent(7));

        Assert.Equal(7, observed);
    }

    [Fact]
    public void Dispose_Subscription_Stops_Future_Delivery()
    {
        SceneEventHub hub = new();
        int observed = 0;

        IDisposable subscription = hub.Subscribe<TestSceneEvent>(evt => observed += evt.Value);
        subscription.Dispose();

        hub.Publish(new TestSceneEvent(5));

        Assert.Equal(0, observed);
    }

    [Fact]
    public void Publish_Delivers_To_Multiple_Subscribers_In_Registration_Order()
    {
        SceneEventHub hub = new();
        List<int> order = new();

        hub.Subscribe<TestSceneEvent>(_ => order.Add(1));
        hub.Subscribe<TestSceneEvent>(_ => order.Add(2));

        hub.Publish(new TestSceneEvent(9));

        Assert.Equal(new[] { 1, 2 }, order);
    }
}
```

- [ ] **Step 4: Run tests to verify they still fail for the right reason**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~SceneEventHubTests" --nologo`

Expected: FAIL with missing-type errors for `SceneEventHub`, not syntax errors in the test file.

- [ ] **Step 5: Commit**

```bash
git add dream.Tests/SceneEventHubTests.cs
git commit -m "test: add scene event hub coverage"
```

### Task 2: Implement the minimal typed event hub

**Files:**
- Create: `dream/SceneEventHub.cs`
- Modify: `dream.Tests/SceneEventHubTests.cs`
- Test: `dream.Tests/SceneEventHubTests.cs`

- [ ] **Step 1: Write the minimal implementation**

```csharp
public sealed class SceneEventHub
{
    private readonly Dictionary<Type, List<Delegate>> _subscriptions = new();

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        Type key = typeof(TEvent);
        if (!_subscriptions.TryGetValue(key, out List<Delegate>? handlers))
        {
            handlers = new List<Delegate>();
            _subscriptions[key] = handlers;
        }

        handlers.Add(handler);
        return new Subscription(() => Unsubscribe(key, handler));
    }

    public void Publish<TEvent>(TEvent eventData)
    {
        if (!_subscriptions.TryGetValue(typeof(TEvent), out List<Delegate>? handlers))
        {
            return;
        }

        Delegate[] snapshot = handlers.ToArray();
        foreach (Delegate handler in snapshot)
        {
            ((Action<TEvent>)handler)(eventData);
        }
    }

    private void Unsubscribe(Type key, Delegate handler)
    {
        if (!_subscriptions.TryGetValue(key, out List<Delegate>? handlers))
        {
            return;
        }

        handlers.Remove(handler);
        if (handlers.Count == 0)
        {
            _subscriptions.Remove(key);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        private bool _disposed;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dispose();
        }
    }
}
```

- [ ] **Step 2: Run the event hub tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~SceneEventHubTests" --nologo`

Expected: PASS for all `SceneEventHubTests`.

- [ ] **Step 3: Add a small explicit lifetime regression test**

Append this test to `dream.Tests/SceneEventHubTests.cs`:

```csharp
[Fact]
public void Disposing_One_Subscription_Does_Not_Remove_Other_Handlers()
{
    SceneEventHub hub = new();
    int kept = 0;

    IDisposable removed = hub.Subscribe<TestSceneEvent>(_ => { });
    hub.Subscribe<TestSceneEvent>(evt => kept = evt.Value);

    removed.Dispose();
    hub.Publish(new TestSceneEvent(11));

    Assert.Equal(11, kept);
}
```

- [ ] **Step 4: Run tests again**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~SceneEventHubTests" --nologo`

Expected: PASS including the new lifetime regression.

- [ ] **Step 5: Commit**

```bash
git add dream/SceneEventHub.cs dream.Tests/SceneEventHubTests.cs
git commit -m "feat: add scene-local event hub"
```

### Task 3: Thread the hub into `PlayScene` without changing gameplay behavior yet

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `docs/architecture.md`
- Test: `dream.Tests/SceneEventHubTests.cs`

- [ ] **Step 1: Add the scene-owned hub field**

Update `dream/PlayScene.cs` near the other fields:

```csharp
private readonly SceneEventHub _events = new();
```

And expose it internally for future wiring:

```csharp
internal SceneEventHub Events => _events;
```

- [ ] **Step 2: Keep construction behavior otherwise unchanged**

The constructor should still keep the same current flow:

```csharp
public PlayScene(GameSettings settings)
{
    _settings = settings;
    _physics = new PhysicsIntegrator();
    _map = new Map(_physics, width: _settings.FloorSize, depth: _settings.FloorSize);
    _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);

    Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
    _player = new Player(_map, _physics, _physics.PlayerBody, playerStartPosition);
    _render = new Render(_settings);
}
```

Do not change orchestration in this phase. The goal is only to establish ownership.

- [ ] **Step 3: Document the new scene-local ownership rule**

Add a short note to `docs/architecture.md` in the `PlayScene` relationship section:

```md
- `PlayScene` is also the intended owner of scene-scoped event publication and subscription lifetime through a local event hub rather than a global engine bus.
```

- [ ] **Step 4: Run build and targeted tests**

Run: `dotnet build dream\\dream.csproj --nologo`

Expected: PASS with the existing BulletSharp warning only.

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~SceneEventHubTests" --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add dream/PlayScene.cs docs/architecture.md
git commit -m "refactor: add scene-owned event hub seam"
```

### Task 4: Record the phase completion

**Files:**
- Create: `docs/ledger/work-log/2026-06-28-scene-event-foundation.md`

- [ ] **Step 1: Write the work-log entry after implementation**

```md
# Scene Event Foundation

Purpose: record the introduction of the first scene-scoped event hub.
Status: completed
Scope: typed publish/subscribe foundation and scene ownership seam

## Summary

Added a small typed `SceneEventHub` and threaded it into `PlayScene` as scene-local infrastructure for later decoupling work.

## Completed Work

- added typed publish/subscribe support
- added explicit subscription disposal behavior
- added focused tests for dispatch order and unsubscribe behavior
- established `PlayScene` as the local owner of event lifetime

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~SceneEventHubTests" --nologo`
```

- [ ] **Step 2: Verify the markdown path and naming**

Run: `Test-Path docs\\ledger\\work-log\\2026-06-28-scene-event-foundation.md`

Expected: `True`

- [ ] **Step 3: Commit**

```bash
git add docs/ledger/work-log/2026-06-28-scene-event-foundation.md
git commit -m "docs: log scene event foundation"
```
