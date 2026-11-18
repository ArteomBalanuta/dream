using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-TRUST-2: a command frame that alternated RequestedMode every tick queued one player-profile SQLite
// write per tick per client. The value-change test in SetSessionInteractionMode cannot stop it - an
// alternating client changes the value by construction - so the write is now paced at one per session
// per second, with the last mode of a paced burst written once the interval passes. The mode itself
// still applies immediately, and the detach save always writes the live one.
public sealed class TrustModeSaveThrottleTests
{
    private const string Player = "Mode Flipper";
    private static readonly PlayerSessionId PlayerSession = new("trust-mode-flipper");

    private static WorldSessionRuntime CreateRuntime(CountingProfiles profiles)
    {
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            persistenceWrites: PersistenceWriteQueue.Inline());
        runtime.WriteCheckpointOnDispose = false;
        runtime.AttachRemoteSession(PlayerSession, Player);
        runtime.RestorePersistedPlayerState(PlayerSession, Player);
        return runtime;
    }

    private static void Tick(WorldSessionRuntime runtime, ulong tick, PlayerInteractionMode? requestedMode, ulong sequence)
    {
        PlayerCommandFrame frame = new(
            PlayerSession,
            sequence,
            new PlayerModeSwitchIntent(requestedMode),
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
        runtime.AdvanceAuthoritativeTick(
            tick,
            SimulationClock.FixedDeltaSeconds,
            frame,
            PlayerSimulationTuning.AuthoritativeDefault);
    }

    [Fact]
    public void ModeFlippedEveryTick_QueuesAtMostOneProfileWritePerSecond()
    {
        CountingProfiles profiles = new();
        using WorldSessionRuntime runtime = CreateRuntime(profiles);
        int savesAfterJoin = profiles.Saves;

        // 128 ticks (2 s) of a client alternating Building and Combat every tick: 128 genuine value
        // changes, which the change test cannot collapse.
        for (ulong tick = 1; tick <= 128; tick++)
        {
            PlayerInteractionMode requested = tick % 2 == 0
                ? PlayerInteractionMode.Building
                : PlayerInteractionMode.Combat;
            Tick(runtime, tick, requested, tick);
        }

        int queued = profiles.Saves - savesAfterJoin;
        Assert.True(queued > 0, "the last mode of a paced burst must still reach the database");
        // One per second of simulation, plus the first change, which is never paced.
        Assert.True(queued <= 4, $"a client-driven mode flip queued {queued} profile writes in 2 s");
        Assert.True(runtime.ModeSavesPaced > 100, $"only {runtime.ModeSavesPaced} writes were paced");

        // The pacing delays the write, it never drops the mode: the last mode of the burst reaches
        // the row within one interval, on ticks that ask for no mode at all.
        PlayerInteractionMode live = runtime.InteractionMode;
        for (ulong tick = 129UL; tick <= 129UL + (ulong)WorldSessionRuntime.ModeSaveIntervalTicks; tick++)
        {
            Tick(runtime, tick, requestedMode: null, tick);
        }

        Assert.Equal(0, runtime.PendingModeSaveCount);
        Assert.Equal(live, runtime.InteractionMode);
        Assert.Equal(live, profiles.TryGet(Player)!.Value.InteractionMode);
        Assert.True(profiles.Saves - savesAfterJoin <= 5);
    }

    [Fact]
    public void AGenuineModeChange_IsPersistedOnce_AndImmediately()
    {
        CountingProfiles profiles = new();
        using WorldSessionRuntime runtime = CreateRuntime(profiles);
        int savesAfterJoin = profiles.Saves;
        PlayerInteractionMode current = runtime.InteractionMode;
        PlayerInteractionMode other = current == PlayerInteractionMode.Building
            ? PlayerInteractionMode.Combat
            : PlayerInteractionMode.Building;

        // A player presses the mode key once, then plays on: the press is persisted on its own tick.
        Tick(runtime, 1UL, other, 1UL);
        Assert.Equal(other, runtime.InteractionMode);
        Assert.Equal(savesAfterJoin + 1, profiles.Saves);
        Assert.Equal(other, profiles.TryGet(Player)!.Value.InteractionMode);

        // The frames that keep re-requesting the mode it now has still write nothing.
        for (ulong tick = 2UL; tick <= 200UL; tick++)
        {
            Tick(runtime, tick, other, tick);
        }

        Assert.Equal(savesAfterJoin + 1, profiles.Saves);
        Assert.Equal(0, runtime.ModeSavesPaced);

        // A second real press a few seconds later is persisted immediately too.
        Tick(runtime, 201UL, current, 201UL);
        Assert.Equal(current, runtime.InteractionMode);
        Assert.Equal(savesAfterJoin + 2, profiles.Saves);
        Assert.Equal(current, profiles.TryGet(Player)!.Value.InteractionMode);
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
