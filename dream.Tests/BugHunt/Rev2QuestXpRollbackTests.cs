using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.BugHunt;

// BH-REV2-3: BH-QUEST-2 moved the XP's profile write INSIDE the turn-in's one closure, so it commits
// with the completion - but GrantQuestExperience still applies the progression to the combatant and
// caches the inflated profile the moment it is ASKED for the write, before the closure has run. When
// that closure's savepoint rolls back, the quest row stays active while the player keeps the XP in
// memory and in the profile cache, and the next unrelated profile save (a detach, which writes
// GetCachedProfile's copy) makes it durable: a rejoin can then hand the same quest in again for the
// same XP. The original fixer flagged this residual honestly; this closes it.
//
// The grant is now watched by a read queued after the closure, and a grant whose write never ran is
// put back on the tick thread: the combatant's progression and the cached profile both go back to what
// they were. These drive GrantQuestExperience and that watch directly (both are private to the
// runtime), on a real write queue and a real persistence bridge.
public sealed class Rev2QuestXpRollbackTests
{
    private const string Player = "Xp Alice";
    private static readonly PlayerSessionId Session = new("rev2-quest-xp");

    private static WorldSessionRuntime CreateRuntime(CountingProfiles profiles, PersistenceWriteQueue writes)
    {
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            persistenceWrites: writes);
        runtime.WriteCheckpointOnDispose = false;
        runtime.AttachRemoteSession(Session, Player);
        runtime.RestorePersistedPlayerState(Session, Player);
        return runtime;
    }

    private static PlayerCombatant Combatant(WorldSessionRuntime runtime)
    {
        MethodInfo resolve = typeof(WorldSessionRuntime).GetMethod(
            "TryResolveAuthoritativePlayerTarget",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(PlayerSessionId), typeof(ConnectedPlayer).MakeByRefType(), typeof(IAuthoritativePlayerTarget).MakeByRefType()],
            modifiers: null)!;
        object?[] args = [Session, null, null];
        Assert.True((bool)resolve.Invoke(runtime, args)!);
        return ((IAuthoritativePlayerTarget)args[2]!).Combatant;
    }

    // Exactly what the turn-in does: ask for the reward write (which applies the progression in memory
    // and caches the inflated profile), then, on an accepted turn-in, watch the grant.
    private static Action? AskForTheRewardWrite(WorldSessionRuntime runtime, long xp)
    {
        MethodInfo grant = typeof(WorldSessionRuntime)
            .GetMethod("GrantQuestExperience", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Action?)grant.Invoke(runtime, [Session, xp]);
    }

    private static void WatchTheGrant(WorldSessionRuntime runtime)
    {
        FieldInfo pending = typeof(WorldSessionRuntime)
            .GetField("_pendingQuestXpGrant", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("the runtime remembers no quest XP grant to watch");
        object watched = pending.GetValue(runtime)
            ?? throw new InvalidOperationException("GrantQuestExperience recorded no grant");
        MethodInfo watch = typeof(WorldSessionRuntime)
            .GetMethod("WatchQuestExperience", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("the runtime does not watch a quest XP grant");
        watch.Invoke(runtime, [watched]);
        pending.SetValue(runtime, null);
    }

    private static void Tick(WorldSessionRuntime runtime, ulong tick) =>
        runtime.AdvanceAuthoritativeTick(
            tick,
            SimulationClock.FixedDeltaSeconds,
            (PlayerCommandFrame?)null,
            PlayerSimulationTuning.AuthoritativeDefault);

    // The defect: the closure rolled back, so its XP write never ran - and the combatant and the cached
    // profile (what the next save writes) kept the XP anyway.
    [Fact]
    public void XpOfATurnInWhoseClosureNeverCommitted_IsTakenBackOutOfMemoryAndTheProfile()
    {
        CountingProfiles profiles = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        using WorldSessionRuntime runtime = CreateRuntime(profiles, writes);
        PlayerCombatant combatant = Combatant(runtime);
        int level = combatant.Level;
        int experience = combatant.Experience;

        Action? reward = AskForTheRewardWrite(runtime, 100);
        Assert.NotNull(reward);
        Assert.True(combatant.Experience > experience || combatant.Level > level);

        // The turn-in's closure rolls back on its savepoint: the reward write never runs.
        WatchTheGrant(runtime);
        Tick(runtime, 1UL);

        Assert.Equal(level, combatant.Level);
        Assert.Equal(experience, combatant.Experience);

        // And the profile the next save would write carries no XP for the quest that stayed active.
        PersistedPlayerProfile saved = profiles.TryGet(Player)!.Value;
        Assert.Equal(level, saved.Stats.Level);
        Assert.Equal(experience, saved.Stats.Experience);

        // A detach saves the live profile: still no XP.
        runtime.DetachRemoteSession(Session);
        saved = profiles.TryGet(Player)!.Value;
        Assert.Equal(experience, saved.Stats.Experience);
    }

    // Control: a turn-in whose closure commits pays its XP, once, in memory and in the row - and a
    // later save (the detach a restart goes through) still shows it once, not twice and not zero.
    [Fact]
    public void XpOfATurnInThatCommitted_IsKept_AndPaidExactlyOnce()
    {
        CountingProfiles profiles = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        using WorldSessionRuntime runtime = CreateRuntime(profiles, writes);
        PlayerCombatant combatant = Combatant(runtime);
        int experience = combatant.Experience;

        Action? reward = AskForTheRewardWrite(runtime, 100);
        Assert.NotNull(reward);
        int granted = combatant.Experience;
        Assert.True(granted > experience);

        // The closure commits: its last step is the reward write.
        reward();
        WatchTheGrant(runtime);
        Tick(runtime, 1UL);

        Assert.Equal(granted, combatant.Experience);
        Assert.Equal(granted, profiles.TryGet(Player)!.Value.Stats.Experience);

        runtime.DetachRemoteSession(Session);
        PersistedPlayerProfile saved = profiles.TryGet(Player)!.Value;
        Assert.Equal(granted, saved.Stats.Experience);
    }

    // The same rollback across the background queue, where the watch read really does run on the
    // persistence worker after the closure. Bounded: the awaited barrier, never a mid-test Flush.
    [Fact]
    public async Task TheRollbackAlsoArrives_WhenTheQueueIsTheBackgroundOne()
    {
        CountingProfiles profiles = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using WorldSessionRuntime runtime = CreateRuntime(profiles, writes);
        PlayerCombatant combatant = Combatant(runtime);
        int level = combatant.Level;
        int experience = combatant.Experience;

        Assert.NotNull(AskForTheRewardWrite(runtime, 250));
        WatchTheGrant(runtime);
        Assert.True(await Dream.Tests.Support.PersistenceFlush.TryAsync(writes));

        for (ulong tick = 1UL; tick <= 4UL && combatant.Experience != experience; tick++)
        {
            Tick(runtime, tick);
        }

        Assert.Equal(level, combatant.Level);
        Assert.Equal(experience, combatant.Experience);
        Assert.True(await Dream.Tests.Support.PersistenceFlush.TryAsync(writes));
        Assert.Equal(experience, profiles.TryGet(Player)!.Value.Stats.Experience);
    }

    // Control: XP earned between the turn-in and the rollback is never erased - the rollback applies
    // only while the progression is still exactly what the grant set.
    [Fact]
    public void ARollbackLeavesProgressionEarnedSinceTheTurnInAlone()
    {
        CountingProfiles profiles = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        using WorldSessionRuntime runtime = CreateRuntime(profiles, writes);
        PlayerCombatant combatant = Combatant(runtime);

        Assert.NotNull(AskForTheRewardWrite(runtime, 100));
        WatchTheGrant(runtime);

        // A kill lands before the tick that applies the rollback.
        int moved = combatant.Experience + 40;
        combatant.ApplyProgression(combatant.Level, moved);
        Tick(runtime, 1UL);

        Assert.Equal(moved, combatant.Experience);
    }

    private sealed class CountingProfiles : IPlayerProfileRepository
    {
        private readonly Dictionary<string, PersistedPlayerProfile> _rows = new(StringComparer.OrdinalIgnoreCase);

        public int Saves { get; private set; }

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            if (!_rows.TryGetValue(playerName, out PersistedPlayerProfile profile))
            {
                profile = PersistedPlayerProfile.CreateDefault(playerName, DateTime.UtcNow);
                _rows[playerName] = profile;
            }

            return profile;
        }

        public PersistedPlayerProfile? TryGet(string playerName) =>
            _rows.TryGetValue(playerName, out PersistedPlayerProfile profile) ? profile : null;

        public void Save(PersistedPlayerProfile profile)
        {
            Saves++;
            _rows[profile.PlayerName] = profile;
        }
    }
}
