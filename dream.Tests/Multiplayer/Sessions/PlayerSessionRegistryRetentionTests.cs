using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Sessions;

// SCALE-7: a client picks a fresh session id on every launch, and the registry used to keep every
// session it ever saw and walk all of them on the tick. Players now lists only the active
// sessions, and sessions that left are remembered in a bounded LRU for a reattach.
public sealed class PlayerSessionRegistryRetentionTests
{
    [Fact]
    public void TenThousandJoinLeaveCycles_KeepTheRegistryBounded()
    {
        PlayerSessionRegistry registry = new();
        registry.Register(PlayerSessionId.LocalDefault, actorId: 1, isLocal: true);
        ConnectedPlayer stays = registry.Register(new PlayerSessionId("stays"), actorId: 2, isLocal: false);

        for (int i = 0; i < 10_000; i++)
        {
            ConnectedPlayer visitor = registry.Register(new PlayerSessionId($"client-{i:D5}"), actorId: 3, isLocal: false);
            visitor.MarkDespawned();
        }

        Assert.Equal(PlayerSessionRegistry.DefaultMaxDepartedSessions, registry.DepartedCount);
        Assert.Equal(PlayerSessionRegistry.DefaultMaxDepartedSessions + 2, registry.Count);
        Assert.Equal(2, registry.ActiveCount);
        Assert.Equal([PlayerSessionId.LocalDefault, stays.SessionId], registry.Players.Select(player => player.SessionId));
        Assert.True(registry.HasSpawnedRemotePlayer());

        // The newest departures are remembered, the oldest forgotten.
        Assert.True(registry.TryGet(new PlayerSessionId("client-09999"), out _));
        Assert.True(registry.TryGet(new PlayerSessionId($"client-{10_000 - PlayerSessionRegistry.DefaultMaxDepartedSessions:D5}"), out _));
        Assert.False(registry.TryGet(new PlayerSessionId($"client-{10_000 - PlayerSessionRegistry.DefaultMaxDepartedSessions - 1:D5}"), out _));
        Assert.False(registry.TryGet(new PlayerSessionId("client-00000"), out _));
    }

    [Fact]
    public void DepartedSessions_AreEvictedOldestDepartureFirst_AndAReturnRefreshesThem()
    {
        PlayerSessionRegistry registry = new(maxDepartedSessions: 2);
        ConnectedPlayer a = registry.Register(new PlayerSessionId("a"), 2, isLocal: false);
        ConnectedPlayer b = registry.Register(new PlayerSessionId("b"), 3, isLocal: false);
        ConnectedPlayer c = registry.Register(new PlayerSessionId("c"), 4, isLocal: false);

        a.MarkDespawned();
        b.MarkDespawned();
        // a comes back and leaves again: it is now the newest departure.
        a.MarkSpawned();
        a.MarkDespawned();
        c.MarkDespawned();

        Assert.False(registry.TryGet(new PlayerSessionId("b"), out _));
        Assert.True(registry.TryGet(new PlayerSessionId("a"), out ConnectedPlayer? remembered));
        Assert.Same(a, remembered);
        Assert.True(registry.TryGet(new PlayerSessionId("c"), out _));
        Assert.Equal(2, registry.Count);
        Assert.Empty(registry.Players);
        Assert.False(registry.HasSpawnedRemotePlayer());

        // A forgotten player object no longer touches the registry.
        b.MarkSpawned();
        Assert.Empty(registry.Players);
        Assert.False(registry.TryAcceptCommand(new PlayerSessionId("b"), 1, out _));
    }

    [Fact]
    public void Players_ListsOnlyActiveSessions_InFirstRegistrationOrder()
    {
        PlayerSessionRegistry registry = new();
        ConnectedPlayer local = registry.Register(PlayerSessionId.LocalDefault, 1, isLocal: true);
        ConnectedPlayer a = registry.Register(new PlayerSessionId("a"), 2, isLocal: false);
        ConnectedPlayer b = registry.Register(new PlayerSessionId("b"), 3, isLocal: false);
        ConnectedPlayer c = registry.Register(new PlayerSessionId("c"), 4, isLocal: false);

        a.MarkDespawned();
        Assert.Equal([local, b, c], registry.Players);
        a.MarkSpawned();
        Assert.Equal([local, a, b, c], registry.Players);

        // The local player stays listed while despawned, as the tick walks always saw it.
        local.MarkDespawned();
        Assert.Equal([local, a, b, c], registry.Players);
        Assert.Equal(0, registry.DepartedCount);

        // Re-registering a session id (a returning former primary given its own seat) replaces the
        // entry in place.
        b.MarkDespawned();
        ConnectedPlayer b2 = registry.Register(new PlayerSessionId("b"), 5, isLocal: false);
        Assert.Equal([local, a, b2, c], registry.Players);
        Assert.Equal(4, registry.Count);
        Assert.Equal(0, registry.DepartedCount);

        // The replaced object is detached from the registry.
        b.MarkSpawned();
        Assert.Equal([local, a, b2, c], registry.Players);
    }

    [Fact]
    public void HasSpawnedRemotePlayer_FollowsSpawnChangesAndReplacements()
    {
        PlayerSessionRegistry registry = new();
        registry.Register(PlayerSessionId.LocalDefault, 1, isLocal: true);
        Assert.False(registry.HasSpawnedRemotePlayer());

        ConnectedPlayer a = registry.Register(new PlayerSessionId("a"), 2, isLocal: false);
        Assert.True(registry.HasSpawnedRemotePlayer());
        a.MarkSpawned();
        a.MarkDespawned();
        a.MarkDespawned();
        Assert.False(registry.HasSpawnedRemotePlayer());

        a.MarkSpawned();
        ConnectedPlayer replacement = registry.Register(new PlayerSessionId("a"), 3, isLocal: false);
        Assert.True(registry.HasSpawnedRemotePlayer());
        replacement.MarkDespawned();
        Assert.False(registry.HasSpawnedRemotePlayer());

        ConnectedPlayer hidden = registry.Register(new PlayerSessionId("h"), 4, isLocal: false, isSpawned: false);
        Assert.False(registry.HasSpawnedRemotePlayer());
        Assert.Equal(2, registry.DepartedCount);
        hidden.MarkSpawned();
        Assert.True(registry.HasSpawnedRemotePlayer());
    }

    [Fact]
    public void WalkingPlayers_DoesNotAllocate()
    {
        PlayerSessionRegistry registry = new();
        registry.Register(PlayerSessionId.LocalDefault, 1, isLocal: true);
        for (int i = 0; i < 8; i++)
        {
            registry.Register(new PlayerSessionId($"p{i}"), 2 + i, isLocal: false);
        }

        int Walk()
        {
            int sum = 0;
            foreach (ConnectedPlayer player in registry.Players)
            {
                sum += player.ActorId;
            }

            return sum;
        }

        Walk();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = Walk() + (registry.HasSpawnedRemotePlayer() ? 1 : 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1 + 2 + 3 + 4 + 5 + 6 + 7 + 8 + 9 + 1, total);
        Assert.Equal(0, allocated);
    }

    // Through the runtime: thousands of fresh session ids come and go, the registry stays bounded
    // and the tick walks only the connected players; a session that comes back within the window
    // still reclaims its released id (wave 10), and one that comes back after it was forgotten
    // joins like a new session with a valid, unshared player id.
    [Fact]
    public void ThousandsOfVisitors_KeepTheRuntimeRegistryBounded_AndReturningSessionsReclaimTheirIds()
    {
        using WorldSessionRuntime runtime = new(32, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");
        int lowId = host.JoinSession("low", "Low").ActorId;
        int earlyId = host.JoinSession("early", "Early").ActorId;
        int recentId = host.JoinSession("recent", "Recent").ActorId;
        Assert.True(lowId < earlyId && earlyId < recentId);

        // "early" leaves first, so the visitors push it out of the window.
        host.ReleaseSession("early");
        host.ReleaseSession("low");
        int visitors = PlayerSessionRegistry.DefaultMaxDepartedSessions + 500;
        for (int i = 0; i < visitors; i++)
        {
            string session = $"client-{i:D5}";
            // Visitors take the lowest released id ("low"'s), so "early"'s stays free.
            Assert.Equal(lowId, host.JoinSession(session, $"Visitor {i}").ActorId);
            host.ReleaseSession(session);
            if (i == visitors - 10)
            {
                host.ReleaseSession("recent");
            }
        }

        PlayerSessionRegistry sessions = runtime.Sessions;
        Assert.True(
            sessions.Count <= PlayerSessionRegistry.DefaultMaxDepartedSessions + sessions.ActiveCount,
            $"registry holds {sessions.Count} sessions");
        Assert.Equal(PlayerSessionRegistry.DefaultMaxDepartedSessions, sessions.DepartedCount);
        // The local player and the primary.
        Assert.Equal(2, sessions.Players.Count);
        Assert.False(sessions.TryGet(new PlayerSessionId("early"), out _));

        // "recent" left within the window: it reclaims its own id.
        JoinAcceptedMessage recent = host.JoinSession("recent", "Recent");
        Assert.Equal(recentId, recent.ActorId);

        // "early" was forgotten: it joins as a new session and takes the lowest free id.
        JoinAcceptedMessage early = host.JoinSession("early", "Early");
        Assert.Equal(lowId, early.ActorId);
        Assert.True(ReplicatedActorIdRanges.IsPlayerId(early.ActorId));

        int[] remoteIds = [.. sessions.Players.Where(player => !player.IsLocal).Select(player => player.ActorId)];
        Assert.Equal(remoteIds.Length, remoteIds.Distinct().Count());
        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        int[] playerActorIds = [.. snapshot.Actors.Where(actor => actor.Kind == CreatureKind.Player).Select(actor => actor.ActorId)];
        Assert.Equal(playerActorIds.Length, playerActorIds.Distinct().Count());
        Assert.Contains(recentId, playerActorIds);
        Assert.Contains(lowId, playerActorIds);
    }
}
