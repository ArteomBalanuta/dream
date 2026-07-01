# Authoritative Actor Combat Event Queue Implementation Plan

Status: proposed

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace immediate arrival-order combat mutation with a deterministic server-tick combat event queue that supports shared enemy damage, future PvP, and rich HP/damage replication to every client.

**Architecture:** Keep the server authoritative. Route inbound `player.action` messages into a validated queue, drain that queue inside the 64 Hz authoritative tick, resolve targets through one actor resolver, mutate health once per ordered event, and replicate both actor snapshots and durable combat event deltas.

**Tech Stack:** C# / .NET 10, existing TCP protocol layer, `WorldSessionRuntime`, `ServerTickRunner`, `PlayerActionMessage`, xUnit.

---

## Current Baseline

Already present:

- shared `WorldSessionRuntime` per server
- authoritative player session registry
- full actor snapshots with health for players and enemies
- `DamageEventDelta` broadcast in `ServerSnapshotEnvelope`
- per-session action sequence rejection
- host-level lock around authoritative runtime access
- enemy and object combat hit resolution
- enemy attacks against nearest alive player target

Missing:

- an explicit server-side action queue
- deterministic per-tick action ordering
- PvP target resolution
- source-aware combat events
- event ids and dedupe semantics
- crit/floating-text style replication
- tests for two players hitting the same enemy in the same authoritative tick

## Target Files

- Create: `dream/Multiplayer/Sessions/IPlayerActionQueue.cs`
- Create: `dream/Multiplayer/Sessions/QueuedPlayerAction.cs`
- Create: `dream/Multiplayer/Sessions/PlayerActionQueue.cs`
- Create: `dream/Simulation/Combat/AuthoritativeCombatTargetResolver.cs`
- Create: `dream/Simulation/Combat/CombatTargetCandidate.cs`
- Create: `dream/Simulation/Combat/CombatTargetPolicy.cs`
- Modify: `dream/Server/DedicatedServerHost.cs`
- Modify: `dream/Server/ITickDrivenAuthoritativeSessionHost.cs`
- Modify: `dream/Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream/Simulation/World/DamageEventDelta.cs`
- Modify: `dream/Simulation/World/ServerSnapshotEnvelope.cs` only if event shape requires naming clarity
- Modify: `dream/Multiplayer/Protocol/PlayerActionHandler.cs`
- Modify: `dream/Multiplayer/Protocol/AuthoritativeActionResult.cs` only if queued action acknowledgement is separated from applied result
- Modify: `dream/Client/Replication/ClientSnapshotDamageApplier.cs`
- Test: `dream.Tests/Multiplayer/Sessions/PlayerActionQueueTests.cs`
- Test: `dream.Tests/Simulation/Combat/AuthoritativeCombatTargetResolverTests.cs`
- Test: `dream.Tests/Simulation/Runtime/WorldSessionRuntimeCombatEventQueueTests.cs`
- Test: `dream.Tests/Server/DedicatedServerHostTickTests.cs`
- Test: `dream.Tests/Client/Replication/ClientAuthoritativeStateApplierTests.cs`

## Task 1: Add A Deterministic Player Action Queue

**Files:**
- Create: `dream/Multiplayer/Sessions/QueuedPlayerAction.cs`
- Create: `dream/Multiplayer/Sessions/IPlayerActionQueue.cs`
- Create: `dream/Multiplayer/Sessions/PlayerActionQueue.cs`
- Create: `dream.Tests/Multiplayer/Sessions/PlayerActionQueueTests.cs`

- [ ] **Step 1: Create queued action DTO**

```csharp
using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public readonly record struct QueuedPlayerAction(
    ulong ReceiveOrder,
    PlayerSessionId SessionId,
    PlayerActionMessage Message);
```

- [ ] **Step 2: Create queue interface**

```csharp
using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public interface IPlayerActionQueue
{
    QueuedPlayerAction Enqueue(PlayerActionMessage message);

    IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds);
}
```

- [ ] **Step 3: Implement queue**

```csharp
using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public sealed class PlayerActionQueue : IPlayerActionQueue
{
    private readonly object _sync = new();
    private readonly List<QueuedPlayerAction> _pending = [];
    private ulong _nextReceiveOrder;

    public QueuedPlayerAction Enqueue(PlayerActionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            QueuedPlayerAction queued = new(
                ++_nextReceiveOrder,
                message.SessionId,
                message);
            _pending.Add(queued);
            return queued;
        }
    }

    public IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds)
    {
        ArgumentNullException.ThrowIfNull(joinedSessionIds);

        HashSet<PlayerSessionId> joined = joinedSessionIds.ToHashSet();
        lock (_sync)
        {
            QueuedPlayerAction[] drained = _pending
                .Where(action => joined.Contains(action.SessionId))
                .OrderBy(action => action.ReceiveOrder)
                .ThenBy(action => action.SessionId.Value, StringComparer.Ordinal)
                .ThenBy(action => action.Message.ActionSequence)
                .ToArray();

            _pending.RemoveAll(action => joined.Contains(action.SessionId));
            return drained;
        }
    }
}
```

- [ ] **Step 4: Add queue ordering tests**

```csharp
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Multiplayer.Sessions;

public class PlayerActionQueueTests
{
    [Fact]
    public void Drain_ReturnsJoinedActions_InReceiveOrder()
    {
        PlayerActionQueue queue = new();
        PlayerSessionId sessionA = new("session-a");
        PlayerSessionId sessionB = new("session-b");

        queue.Enqueue(new PlayerActionMessage(sessionB, 2, new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ)));
        queue.Enqueue(new PlayerActionMessage(sessionA, 1, new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ)));

        IReadOnlyList<QueuedPlayerAction> drained = queue.Drain([sessionA, sessionB]);

        Assert.Equal("session-b", drained[0].SessionId.Value);
        Assert.Equal("session-a", drained[1].SessionId.Value);
    }

    [Fact]
    public void Drain_IgnoresUnjoinedSessions_AndKeepsThemPending()
    {
        PlayerActionQueue queue = new();
        PlayerSessionId joined = new("joined");
        PlayerSessionId unjoined = new("unjoined");

        queue.Enqueue(new PlayerActionMessage(unjoined, 1, new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(12, 2f, 10))));
        queue.Enqueue(new PlayerActionMessage(joined, 2, new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(13, 2f, 10))));

        IReadOnlyList<QueuedPlayerAction> firstDrain = queue.Drain([joined]);
        IReadOnlyList<QueuedPlayerAction> secondDrain = queue.Drain([unjoined]);

        Assert.Single(firstDrain);
        Assert.Equal("joined", firstDrain[0].SessionId.Value);
        Assert.Single(secondDrain);
        Assert.Equal("unjoined", secondDrain[0].SessionId.Value);
    }
}
```

- [ ] **Step 5: Run queue tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PlayerActionQueueTests"
```

Expected: pass.

## Task 2: Queue Actions In The Server Host

**Files:**
- Modify: `dream/Server/DedicatedServerHost.cs`
- Modify: `dream/Multiplayer/Protocol/PlayerActionHandler.cs`
- Modify: `dream/Multiplayer/Protocol/ServerRoutingContext.cs`
- Modify: `dream.Tests/Server/DedicatedServerHostTickTests.cs`

- [ ] **Step 1: Add action queue to routing context**

Add to `ServerRoutingContext`:

```csharp
public IPlayerActionQueue? ActionQueue { get; init; }
```

Update the context constructor to accept and store `IPlayerActionQueue? actionQueue`.

- [ ] **Step 2: Add queue to `DedicatedServerHost`**

Add:

```csharp
private readonly IPlayerActionQueue _actionQueue = new PlayerActionQueue();
```

Pass `_actionQueue` into new `ServerRoutingContext` instances.

- [ ] **Step 3: Change `PlayerActionHandler` to enqueue**

After join/session validation, replace immediate `context.Authority.ProcessAction(actionMessage)` with:

```csharp
if (context.ActionQueue == null)
{
    context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
        actionMessage,
        serverTick: 0,
        "Authoritative action queue is not attached to the server host yet.")));
    return;
}

context.ActionQueue.Enqueue(actionMessage);
```

Do not send an accepted `ServerActionMessage` yet. The authoritative applied result must come from the tick that drains the queue. Keep immediate rejection responses for invalid joins, wrong sessions, and missing runtime/queue.

- [ ] **Step 4: Drain queued actions with command frames**

In `DedicatedServerHost.AdvanceTime(...)`, drain actions for the same joined session ids used for commands:

```csharp
PlayerSessionId[] joinedSessionIds = ResolveJoinedSessionIds();
IReadOnlyList<PlayerCommandFrame> pendingCommands = _commandInbox.Drain(joinedSessionIds);
IReadOnlyList<QueuedPlayerAction> pendingActions = _actionQueue.Drain(joinedSessionIds);
tickDrivenAuthority.AdvanceTick(
    tick,
    fixedDeltaSeconds,
    pendingCommands,
    pendingActions,
    DefaultServerTuning,
    allowGameplaySimulation: true);
```

- [ ] **Step 5: Run server host tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~DedicatedServerHostTickTests|FullyQualifiedName~AuthoritativeActionFlowTests"
```

Expected: existing immediate-action acceptance tests fail until Task 3 updates the host/runtime contract.

## Task 3: Apply Queued Actions Inside The Authoritative Tick

**Files:**
- Modify: `dream/Server/ITickDrivenAuthoritativeSessionHost.cs`
- Modify: `dream/Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream.Tests/Simulation/Runtime/WorldSessionRuntimeCombatEventQueueTests.cs`

- [ ] **Step 1: Extend tick host interface**

```csharp
void AdvanceTick(
    ulong serverTick,
    float deltaTime,
    IReadOnlyList<PlayerCommandFrame> commandFrames,
    IReadOnlyList<QueuedPlayerAction> queuedActions,
    PlayerSimulationTuning playerTuning,
    bool allowGameplaySimulation = true);
```

Keep an overload without queued actions if it reduces test churn:

```csharp
void AdvanceTick(
    ulong serverTick,
    float deltaTime,
    IReadOnlyList<PlayerCommandFrame> commandFrames,
    PlayerSimulationTuning playerTuning,
    bool allowGameplaySimulation = true)
    => AdvanceTick(serverTick, deltaTime, commandFrames, Array.Empty<QueuedPlayerAction>(), playerTuning, allowGameplaySimulation);
```

- [ ] **Step 2: Add runtime drain method**

Add to `WorldSessionRuntime`:

```csharp
public IReadOnlyList<AuthoritativeActionResult> ProcessQueuedAuthoritativeActions(
    ulong serverTick,
    IReadOnlyList<QueuedPlayerAction> queuedActions)
{
    CurrentServerTick = serverTick;
    List<AuthoritativeActionResult> results = [];

    foreach (QueuedPlayerAction action in queuedActions
        .OrderBy(action => action.ReceiveOrder)
        .ThenBy(action => action.SessionId.Value, StringComparer.Ordinal)
        .ThenBy(action => action.Message.ActionSequence))
    {
        results.Add(ProcessAuthoritativeAction(action.Message));
    }

    return results;
}
```

- [ ] **Step 3: Process actions after command/facing state and before enemy AI**

In `RuntimeAuthoritativeSessionHost.AdvanceTick(...)`, keep movement command advancement first so authoritative actor position/facing are current for that tick. Then call:

```csharp
Runtime.ProcessQueuedAuthoritativeActions(serverTick, queuedActions);
```

Do this before enemy AI effects are exported in the snapshot for that tick.

- [ ] **Step 4: Add same-tick two-player enemy damage test**

Create `dream.Tests/Simulation/Runtime/WorldSessionRuntimeCombatEventQueueTests.cs`:

```csharp
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

public class WorldSessionRuntimeCombatEventQueueTests
{
    [Fact]
    public void ProcessQueuedAuthoritativeActions_Applies_TwoPlayersAgainstSameEnemy_InQueueOrder()
    {
        using WorldSessionRuntime runtime = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));

        Vector3 wolfAnchor = new(5.5f, 1.75f, 2.5f);
        runtime.AdvanceAuthoritativeTick(
            1,
            SimulationClock.FixedDeltaSeconds,
            CreateCombatCommand("session-a", 1, DirectionFrom(new Vector3(5f, 1.85f, 5f), wolfAnchor)),
            CreatePlayerTuning());

        MoveSecondaryIntoWolfRange(runtime);
        runtime.AdvanceSecondaryRemoteSession(
            CreateCombatCommand("session-b", 50, DirectionFrom(new Vector3(5.82f, 1.85f, 4.24f), wolfAnchor)),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        ServerSnapshotEnvelope before = runtime.ExportServerSnapshotEnvelope();
        ActorSnapshot wolfBefore = Assert.Single(before.Actors, actor => actor.Kind == CreatureKind.Enemy);

        IReadOnlyList<AuthoritativeActionResult> results = runtime.ProcessQueuedAuthoritativeActions(
            2,
            [
                new QueuedPlayerAction(1, new PlayerSessionId("session-a"), new PlayerActionMessage(new PlayerSessionId("session-a"), 2, new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ))),
                new QueuedPlayerAction(2, new PlayerSessionId("session-b"), new PlayerActionMessage(new PlayerSessionId("session-b"), 2, new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)))
            ]);

        ServerSnapshotEnvelope after = runtime.ExportServerSnapshotEnvelope();
        ActorSnapshot wolfAfter = Assert.Single(after.Actors, actor => actor.Kind == CreatureKind.Enemy);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.True(wolfAfter.CurrentHealth < wolfBefore.CurrentHealth);
        Assert.True(after.DamageEvents.Count >= 2);
    }

    private static void MoveSecondaryIntoWolfRange(WorldSessionRuntime runtime)
    {
        Vector3 movementFacing = DirectionFrom(new Vector3(7f, 1.85f, 7f), new Vector3(5.82f, 1.85f, 4.24f));
        for (ulong sequence = 2; sequence < 50; sequence++)
        {
            runtime.AdvanceSecondaryRemoteSession(
                new PlayerCommandFrame(
                    new PlayerSessionId("session-b"),
                    sequence,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                    new PlayerMoveIntent(1f, 0f, 0f),
                    new PlayerLookIntent(0f, 0f, movementFacing),
                    PrimaryUsePressed: false),
                SimulationClock.FixedDeltaSeconds,
                CreatePlayerTuning());
        }
    }

    private static PlayerCommandFrame CreateCombatCommand(string sessionId, ulong sequence, Vector3 facing)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(sessionId),
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            PlayerMoveIntent.None,
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? -Vector3.UnitZ : direction.Normalized();
    }

    private static PlayerSimulationTuning CreatePlayerTuning()
    {
        return new PlayerSimulationTuning(0.35f, 2.5f, 6f, 18f, 5f);
    }
}
```

- [ ] **Step 5: Run queued action runtime tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~WorldSessionRuntimeCombatEventQueueTests"
```

Expected: pass after the queue is integrated.

## Task 4: Add Source-Aware Combat Events

**Files:**
- Modify: `dream/Simulation/World/DamageEventDelta.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream/Client/Replication/ClientSnapshotDamageApplier.cs`
- Modify: protocol serialization tests

- [ ] **Step 1: Expand damage event shape**

Replace `DamageEventDelta` with:

```csharp
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;

namespace Dream.Simulation.World;

public readonly record struct DamageEventDelta(
    ulong EventId,
    ulong ServerTick,
    string? SourceSessionId,
    int SourceActorId,
    int TargetActorId,
    CreatureKind TargetKind,
    int Amount,
    bool ActorDied,
    int CurrentHealth,
    int MaxHealth,
    ulong? ActionSequence,
    FloatingCombatTextKind TextKind);
```

- [ ] **Step 2: Track event id in runtime**

Add to `WorldSessionRuntime`:

```csharp
private ulong _nextCombatEventId;
```

Create damage deltas from the combat action path, where the source combatant and action sequence are known, instead of relying only on generic `DamageAppliedEvent` subscriptions.

- [ ] **Step 3: Preserve scene-local events for presentation**

Keep `DamageAppliedEvent` for local scene subscribers. Treat `DamageEventDelta` as network replication data, not as the only event system in the engine.

- [ ] **Step 4: Update client damage applier**

Use `damageEvent.TextKind` instead of always publishing `FloatingCombatTextKind.Regular`.

- [ ] **Step 5: Run protocol and client replication tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~ProtocolFoundationTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests|FullyQualifiedName~RemoteEnemyStateApplierTests"
```

Expected: pass with updated constructors and serialized fields.

## Task 5: Introduce Unified Combat Target Resolution

**Files:**
- Create: `dream/Simulation/Combat/CombatTargetCandidate.cs`
- Create: `dream/Simulation/Combat/CombatTargetPolicy.cs`
- Create: `dream/Simulation/Combat/AuthoritativeCombatTargetResolver.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Create: `dream.Tests/Simulation/Combat/AuthoritativeCombatTargetResolverTests.cs`

- [ ] **Step 1: Add target policy**

```csharp
namespace Dream.Simulation.Combat;

public readonly record struct CombatTargetPolicy(bool PlayerVsPlayerEnabled)
{
    public static CombatTargetPolicy DefaultPve => new(PlayerVsPlayerEnabled: false);
}
```

- [ ] **Step 2: Add target candidate**

```csharp
using OpenTK.Mathematics;

namespace Dream.Simulation.Combat;

public readonly record struct CombatTargetCandidate(
    CombatActorHandle Actor,
    Vector3 Anchor,
    int Priority);
```

- [ ] **Step 3: Implement resolver**

```csharp
using OpenTK.Mathematics;

namespace Dream.Simulation.Combat;

public static class AuthoritativeCombatTargetResolver
{
    public static CombatActorHandle? TryResolve(
        Vector3 origin,
        Vector3 direction,
        float maxHitDistance,
        IEnumerable<CombatTargetCandidate> candidates)
    {
        if (direction.LengthSquared <= 0.0001f || maxHitDistance <= 0f)
        {
            return null;
        }

        Vector3 normalized = direction.Normalized();
        CombatTargetCandidate? best = null;
        float bestDistance = maxHitDistance;

        foreach (CombatTargetCandidate candidate in candidates.OrderBy(candidate => candidate.Priority))
        {
            if (!candidate.Actor.Damageable.Health.IsAlive)
            {
                continue;
            }

            Vector3 toTarget = candidate.Anchor - origin;
            float projectedDistance = Vector3.Dot(toTarget, normalized);
            if (projectedDistance < 0f || projectedDistance > bestDistance)
            {
                continue;
            }

            Vector3 closestPoint = origin + normalized * projectedDistance;
            if ((candidate.Anchor - closestPoint).Length > 0.9f)
            {
                continue;
            }

            best = candidate;
            bestDistance = projectedDistance;
        }

        return best?.Actor;
    }
}
```

- [ ] **Step 4: Generate candidates in runtime**

In `WorldSessionRuntime`, build target candidates from:

- enemies with priority `10`
- other players with priority `20` only when `CombatTargetPolicy.PlayerVsPlayerEnabled`
- destructible world objects with priority `30`

Exclude the acting player's own actor id.

- [ ] **Step 5: Keep PvP disabled by default**

Add a runtime field:

```csharp
private CombatTargetPolicy _combatTargetPolicy = CombatTargetPolicy.DefaultPve;
```

Add a method for tests and future server settings:

```csharp
public void SetCombatTargetPolicy(CombatTargetPolicy policy)
{
    _combatTargetPolicy = policy;
}
```

- [ ] **Step 6: Add PvP disabled/enabled tests**

Test that a player ray crossing another player does not damage them with default policy, then damages them when `PlayerVsPlayerEnabled` is true.

- [ ] **Step 7: Run combat resolver tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthoritativeCombatTargetResolverTests|FullyQualifiedName~CombatUseResolverTests|FullyQualifiedName~AuthoritativeActionFlowTests"
```

Expected: pass.

## Task 6: Broadcast Applied Action Results Or Rely On Rich Snapshot Events

**Files:**
- Modify: `dream/Server/DedicatedServerService.cs`
- Modify: `dream/Server/ServerConnection.cs`
- Modify: `dream/Simulation/World/ServerSnapshotEnvelope.cs` if action results are embedded
- Modify tests under `dream.Tests/Server/`

- [ ] **Step 1: Choose the first implementation shape**

Use snapshot events as the first durable broadcast path. Do not broadcast `ServerActionMessage` to every client yet.

Reason:

- snapshots already go to all joined clients
- actor state and combat deltas stay ordered together
- action result replies can remain useful for rejection/ack later, but they are not required for all-client HP convergence

- [ ] **Step 2: Add applied event ids to snapshot diagnostics**

Ensure every damage event in a snapshot has `ServerTick` equal to the tick where it was applied.

- [ ] **Step 3: Keep immediate rejection responses**

Invalid session, unjoined session, stale action sequence, wrong mode, cooldown, and out-of-range actions should still produce rejections. If queued actions cannot reject until tick drain, include those rejections in a per-connection result queue or snapshot-local rejection stream in a later task.

- [ ] **Step 4: Add all-clients receive HP delta test**

At service level, attach two joined `ServerConnection` fakes, queue one combat event, advance once, and assert both connections receive a snapshot with the same damage event and same actor HP.

## Final Verification

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected:

- all tests pass
- build succeeds with known warnings only
- two same-tick player attacks against one enemy are resolved deterministically
- every damage event has source, target, event id, action sequence, and server tick
- enemy and player HP remain authoritative in actor snapshots
- PvP remains disabled by default but has a tested server-side policy switch

## Success Criteria

- The server treats combat as ordered authoritative events, not incidental connection-loop side effects.
- Players, enemies, and destructible objects share one target-resolution model.
- Two players attacking the same wolf in the same tick produce deterministic health results.
- Clients receive both state convergence through actor snapshots and edge-triggered damage facts through combat events.
- The engine can add PvP deliberately without trusting clients or rewriting combat from scratch.
