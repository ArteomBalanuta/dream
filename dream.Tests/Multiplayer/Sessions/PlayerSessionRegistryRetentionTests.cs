using OpenTK.Mathematics;
using Xunit;
using Dream.Tests.Utilities;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
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

        // One warm-up call is far short of the runtime's tier-up threshold, so a single window of
        // one more call charges the tiering transition to the measurement. AllocationAssert warms
        // past the threshold and keeps the cleanest of several rounds; the walk is read-only, so
        // every round does the same work and the figure is the steady state the gate is about.
        int total = Walk() + (registry.HasSpawnedRemotePlayer() ? 1 : 0);
        Assert.Equal(1 + 2 + 3 + 4 + 5 + 6 + 7 + 8 + 9 + 1, total);

        int sink = 0;
        AllocationAssert.AtMost(0, () => sink += Walk() + (registry.HasSpawnedRemotePlayer() ? 1 : 0));
        Assert.NotEqual(0, sink);
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

    // Fix loop 1: whoever keeps state per session id learns when the registry drops an entry,
    // both for an LRU eviction and for a replacement under the same id.
    [Fact]
    public void SessionForgotten_ReportsEvictionsAndReplacements()
    {
        List<string> forgotten = [];
        PlayerSessionRegistry registry = new(maxDepartedSessions: 1)
        {
            SessionForgotten = player => forgotten.Add(player.SessionId.Value)
        };
        ConnectedPlayer a = registry.Register(new PlayerSessionId("a"), 2, isLocal: false);
        ConnectedPlayer b = registry.Register(new PlayerSessionId("b"), 3, isLocal: false);
        a.MarkDespawned();
        Assert.Empty(forgotten);
        b.MarkDespawned();
        Assert.Equal(["a"], forgotten);

        ConnectedPlayer b2 = registry.Register(new PlayerSessionId("b"), 4, isLocal: false);
        Assert.Equal(["a", "b"], forgotten);
        Assert.True(registry.TryGet(new PlayerSessionId("b"), out ConnectedPlayer? current));
        Assert.Same(b2, current);
    }

    // Fix loop 1 (reviewer S1): the runtime's per-session interaction modes follow the registry, so
    // they stay bounded however many session ids come and go.
    [Fact]
    public void ThousandsOfVisitors_KeepTheRuntimeInteractionModesBoundedByTheRegistry()
    {
        using WorldSessionRuntime runtime = new(32, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");
        int visitors = PlayerSessionRegistry.DefaultMaxDepartedSessions + 300;
        for (int i = 0; i < visitors; i++)
        {
            string session = $"client-{i:D5}";
            host.JoinSession(session, $"Visitor {i}");
            host.ReleaseSession(session);
        }

        PlayerSessionRegistry sessions = runtime.Sessions;
        Assert.Equal(PlayerSessionRegistry.DefaultMaxDepartedSessions, sessions.DepartedCount);
        // One mode per remembered session (the local player, the primary and the departed ones).
        Assert.Equal(sessions.Count, runtime.SessionInteractionModeCount);

        // A remembered visitor comes back on its entry; a forgotten one joins anew.
        host.JoinSession($"client-{visitors - 1:D5}", $"Visitor {visitors - 1}");
        host.JoinSession("client-00000", "Visitor 0");
        Assert.True(sessions.Count <= PlayerSessionRegistry.DefaultMaxDepartedSessions + sessions.ActiveCount);
        Assert.Equal(sessions.Count, runtime.SessionInteractionModeCount);
    }

    // Fix loop 1: the profile cache is keyed by player name, which the client chooses, so it keeps
    // every active player's profile and only the most recently departed players' beyond that. An
    // evicted profile was saved on detach; a later join of that name reads it from the repository.
    [Fact]
    public void ProfileCache_KeepsActivePlayers_AndAnLruOfDepartedNames()
    {
        const int MaxDeparted = 4;
        ProfileRepository profiles = new();
        using WorldSessionRuntime runtime = new(32, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), playerProfiles: profiles)
        {
            MaxDepartedProfiles = MaxDeparted
        };
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        profiles.Save(Profile("Visitor 0", strength: 13, PlayerInteractionMode.Combat));
        host.JoinSession("primary", "Primary");

        for (int i = 0; i < 20; i++)
        {
            host.JoinSession($"client-{i:D2}", $"Visitor {i}");
            host.ReleaseSession($"client-{i:D2}");
        }

        Assert.Equal(MaxDeparted, runtime.DepartedProfileCount);
        // The primary (active, never evicted) and the last MaxDeparted visitors.
        Assert.Equal(1 + MaxDeparted, runtime.ProfileCacheCount);

        // Visitor 0 was saved on detach (nothing lost) and evicted since.
        PersistedPlayerProfile saved = profiles.TryGet("Visitor 0")!.Value;
        Assert.Equal(13, saved.Stats.Strength);
        Assert.Equal(PlayerInteractionMode.Combat, saved.InteractionMode);

        // Its next join reads the repository, not a stale cached copy.
        profiles.Save(saved with { Stats = saved.Stats with { Strength = 14 } });
        host.JoinSession("client-back", "Visitor 0");
        // Two active profiles plus the departed ones.
        Assert.Equal(MaxDeparted, runtime.DepartedProfileCount);
        Assert.Equal(2 + MaxDeparted, runtime.ProfileCacheCount);
        host.ReleaseSession("client-back");
        PersistedPlayerProfile resaved = profiles.TryGet("Visitor 0")!.Value;
        Assert.Equal(14, resaved.Stats.Strength);
        Assert.Equal(PlayerInteractionMode.Combat, resaved.InteractionMode);
        Assert.Equal(MaxDeparted, runtime.DepartedProfileCount);
        Assert.Equal(1 + MaxDeparted, runtime.ProfileCacheCount);

        // The primary stayed cached through the churn; once it leaves it joins the LRU.
        host.ReleaseSession("primary");
        Assert.NotNull(profiles.TryGet("Primary"));
        Assert.Equal(MaxDeparted, runtime.DepartedProfileCount);
        Assert.Equal(MaxDeparted, runtime.ProfileCacheCount);

        // A remembered name that joins again leaves the eviction order while it is active.
        host.JoinSession("primary-again", "Primary");
        Assert.Equal(MaxDeparted - 1, runtime.DepartedProfileCount);
        Assert.Equal(MaxDeparted, runtime.ProfileCacheCount);
    }

    private static PersistedPlayerProfile Profile(string name, int strength, PlayerInteractionMode mode)
    {
        return new PersistedPlayerProfile(
            name,
            name,
            mode,
            new PersistedCreatureStats(100, 100, strength, 10, 10, 10, 6f, 2f, 1),
            DateTime.UtcNow,
            DateTime.UtcNow);
    }

    private sealed class ProfileRepository : IPlayerProfileRepository
    {
        private readonly Dictionary<string, PersistedPlayerProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            if (!_profiles.TryGetValue(playerName, out PersistedPlayerProfile profile))
            {
                profile = PersistedPlayerProfile.CreateDefault(playerName, DateTime.UtcNow);
                _profiles[playerName] = profile;
            }

            return profile;
        }

        public PersistedPlayerProfile? TryGet(string playerName)
        {
            return _profiles.TryGetValue(playerName, out PersistedPlayerProfile profile) ? profile : null;
        }

        public void Save(PersistedPlayerProfile profile)
        {
            _profiles[profile.PlayerName] = profile;
        }
    }
}
