using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Owner-only progress and the character presence lease (M8 workstream C).
//
// With OwnerOnlyProgress on, the actor snapshots that go on the wire stop carrying experience and base
// stats: CombatStats is null and Progression is (Level, -1, -1), so other players see a level and nothing
// else. The owner gets the real values as ServerPlayerProgress, a session message on the vitals cadence.
// CaptureActorSnapshots itself does not change, because SimulationStateChecksum hashes it.
public sealed partial class WorldSessionRuntime
{
    // Experience marks a projected progression: the client keeps what it knows when it sees it.
    public const int HiddenExperience = -1;

    // How often (in ticks) the lease renewal is *looked at*: 1 s at the 64 Hz tick. Whether a renewal is
    // actually due is decided on the wall clock (RuntimePersistenceBridge.PresenceRenewInterval), the unit
    // the lease expiry uses; this cadence only has to be well inside it even on a host whose ticks are
    // falling behind real time (BH-XFER-4).
    public const ulong PresencePumpIntervalTicks = 64;

    // How often (in ticks) the connected accounts' standing is read again by default: 15 s at 64 Hz. It
    // used to be the presence cadence too; the lease is now renewed on the wall clock instead.
    public const ulong StandingRefreshDefaultIntervalTicks = 960;

    private readonly record struct ProgressSent(
        int Level,
        int Experience,
        int ExperienceToNextLevel,
        PlayerCombatStatsSnapshot Stats);

    private readonly Dictionary<PlayerSessionId, ProgressSent> _lastSentProgress = [];
    private readonly List<string> _presenceKeysScratch = [];

    // The accounts the runtime was built with (server commands write sanctions through it); null without.
    internal Dream.Simulation.Persistence.IAccountRepository? AccountRepository => _persistence.Accounts;

    // The actors as clients see them. Without OwnerOnlyProgress that is CaptureActorSnapshots unchanged.
    // Applied at every wire point (snapshot, resync, join), before the actor delta chain diffs them.
    internal IReadOnlyList<ActorSnapshot> CaptureWireActorSnapshots() => ProjectForWire(CaptureActorSnapshots());

    private IReadOnlyList<ActorSnapshot> ProjectForWire(IReadOnlyList<ActorSnapshot> actors)
    {
        if (!OwnerOnlyProgress)
        {
            return actors;
        }

        // CaptureActorSnapshots hands out a fresh list, so it is edited in place.
        List<ActorSnapshot> list = actors as List<ActorSnapshot> ?? [.. actors];
        for (int i = 0; i < list.Count; i++)
        {
            ActorSnapshot actor = list[i];
            if (actor.Kind != Dream.Simulation.Combat.CreatureKind.Player)
            {
                continue;
            }

            list[i] = actor with
            {
                CombatStats = null,
                Progression = actor.Progression is { } progression
                    ? new PlayerProgressionSnapshot(progression.Level, HiddenExperience, HiddenExperience)
                    : null,
            };
        }

        return list;
    }

    // On the vitals cadence, queues ServerPlayerProgress for the sessions whose values changed (and for a
    // session that has not been sent any yet: the first one follows the join).
    private void StepOwnerProgress(ulong tick)
    {
        if (!OwnerOnlyProgress || tick % VitalsIntervalTicks != 0)
        {
            return;
        }

        PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
        if (primarySessionId != LocalPlayerSessionId)
        {
            CollectOwnerProgress(primarySessionId, Player.Combatant);
        }

        RemoteSessionPlayerState[] secondaries = _orderedSecondaryRemotePlayers;
        for (int i = 0; i < secondaries.Length; i++)
        {
            CollectOwnerProgress(secondaries[i].SessionId, secondaries[i].Combatant);
        }
    }

    private void CollectOwnerProgress(PlayerSessionId sessionId, PlayerCombatant combatant)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) || player == null || !player.IsSpawned)
        {
            return;
        }

        ProgressSent now = new(
            combatant.Level,
            combatant.Experience,
            EvaluateSnapshotExperienceToNextLevel(combatant),
            PlayerCombatStatsSnapshot.From(combatant.BaseStats));
        if (_lastSentProgress.TryGetValue(sessionId, out ProgressSent last) && last == now)
        {
            return;
        }

        _lastSentProgress[sessionId] = now;
        QueueSessionMessage(
            sessionId,
            new ServerPlayerProgress(now.Level, now.Experience, now.ExperienceToNextLevel, now.Stats));
    }

    private void ForgetOwnerProgress(PlayerSessionId session)
    {
        _lastSentProgress.Remove(session);
    }

    // Looks at the lease renewal every second of simulated time; the renewal itself is due on the wall
    // clock, so a host whose ticks fall behind real time cannot stretch it past the lease (BH-XFER-4).
    private void StepPresence(ulong tick)
    {
        if (tick % PresencePumpIntervalTicks != 0 || !HasAccounts || !_persistence.IsPresenceRenewalDue())
        {
            return;
        }

        _presenceKeysScratch.Clear();
        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (!player.IsLocal && player.IsSpawned)
            {
                _presenceKeysScratch.Add(player.CharacterKey.Value);
            }
        }

        _persistence.RenewPresence(_presenceKeysScratch);
        _presenceKeysScratch.Clear();
    }

    private void ReleasePresenceOnDetach(string characterKey)
    {
        if (HasAccounts)
        {
            _persistence.ReleasePresence(characterKey);
        }
    }

    // A join was accepted: takes the lease again, behind any release the detach of a taken-over seat queued.
    internal void ClaimPresenceAfterJoin(string characterKey)
    {
        if (HasAccounts)
        {
            _persistence.ClaimPresenceAfterJoin(characterKey);
        }
    }

    // BH-XFER-4. The next character whose renewal found this world no longer holding its lease (it ran out
    // while this host was stalled, or another world took it over). The host closes that connection: a seat
    // that is not the lease's owner must not keep playing, because the profile row it writes is global.
    internal bool TryTakeLostPresence(out string characterKey)
    {
        if (!HasAccounts)
        {
            characterKey = string.Empty;
            return false;
        }

        return _persistence.TryTakeLostPresence(out characterKey);
    }

    // A join the host refused after its prefetch took the lease.
    internal void ReleasePresenceOfRejectedJoin(string typedName, Dream.Simulation.Characters.AccountKey? account)
    {
        if (HasAccounts)
        {
            _persistence.ReleasePresenceOfRejectedJoin(typedName, account);
        }
    }

    // Shutdown: queues the release of every lease this world holds, before the write queue drains.
    internal void ReleaseAllPresence()
    {
        if (!HasAccounts)
        {
            return;
        }

        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (!player.IsLocal && player.IsSpawned)
            {
                _persistence.ReleasePresence(player.CharacterKey.Value);
            }
        }
    }
}
