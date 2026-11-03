using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// MEM-7 cached the sorted roster (re-sorted only when it changes) and reuses the lists that
// BuildPlayerRoster and BuildRemotePlayerStates return. This drives one presenter through a long
// scripted session (joins, leaves, renames, a reconnect as another session, and long runs of
// snapshots that leave the roster alone) and checks every frame against a model of the old,
// allocate-every-call presenter. The roster oracle is the old LINQ expression, applied to the
// model's roster rather than to the presenter's own output.
public class ClientScenePresenterLongSessionIntegrationTests
{
    private const int Frames = 1500;

    [Fact]
    public void LongScriptedSession_RosterAndRemoteStates_MatchTheOldPresenterEveryFrame()
    {
        Random random = new(7_27_2026);
        ScriptedSession session = new(random);
        ClientScenePresenter presenter = new(session.LocalSessionId);
        ReferencePresenter reference = new(session.LocalSessionId);

        JoinAcceptedMessage firstJoin = session.CreateJoin(serverTick: 1);
        presenter.ApplyJoinAccepted(firstJoin);
        reference.ApplyJoinAccepted(firstJoin);

        int rosterRebuilds = 0;
        int unchangedRosterFrames = 0;
        int localChanges = 0;
        PlayerRosterEntry[] previousRosterCopy = presenter.BuildPlayerRoster().ToArray();
        ulong tick = 1;
        for (int frame = 0; frame < Frames; frame++)
        {
            tick++;
            ScriptEvent scriptEvent = session.Step(frame);
            if (scriptEvent == ScriptEvent.LocalSessionChanged)
            {
                // A reconnect as a different session: the join resets the timeline and roster order.
                JoinAcceptedMessage join = session.CreateJoin(tick);
                presenter.ApplyJoinAccepted(join);
                reference.ApplyJoinAccepted(join);
                localChanges++;
            }
            else
            {
                ServerSnapshotEnvelope snapshot = session.CreateSnapshot(tick);
                presenter.ApplySnapshot(snapshot);
                reference.ApplySnapshot(snapshot);
            }

            // Several render frames per snapshot, between ticks.
            for (int render = 0; render < 2; render++)
            {
                double renderTick = tick - 2 + render * 0.5;
                IReadOnlyList<RemotePlayerPresentationState> states = presenter.BuildRemotePlayerStates(renderTick);
                // The reference is the presenter as it was before the avatar fields (held items, serials,
                // speed, death age); parity is over the fields it had.
                Assert.Equal(
                    reference.BuildRemotePlayerStates(renderTick),
                    states.Select(state => state with
                    {
                        MainHandVisual = null,
                        OffHandVisual = null,
                        AttackSerial = 0,
                        HitSerial = 0,
                        Airborne = false,
                        HorizontalSpeed = 0f,
                        DeathAgeSeconds = float.PositiveInfinity,
                        TravelDirection = default
                    }).ToArray());
                // The reused list is refilled, never appended to.
                Assert.Same(states, presenter.BuildRemotePlayerStates(renderTick));
                Assert.Equal(states.Count, states.Select(state => state.ActorId).Distinct().Count());
            }

            IReadOnlyList<PlayerRosterEntry> roster = presenter.BuildPlayerRoster();
            PlayerRosterEntry[] expected = reference.Roster
                .OrderByDescending(entry => string.Equals(entry.SessionId, reference.LocalSessionId, StringComparison.Ordinal))
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected, roster.ToArray());
            Assert.Same(roster, presenter.BuildPlayerRoster());

            if (expected.SequenceEqual(previousRosterCopy))
            {
                unchangedRosterFrames++;
            }
            else
            {
                rosterRebuilds++;
            }

            previousRosterCopy = roster.ToArray();
        }

        // The script really covered every kind of change and long unchanged stretches.
        Assert.True(session.Joins >= 20, $"joins {session.Joins}");
        Assert.True(session.Leaves >= 20, $"leaves {session.Leaves}");
        Assert.True(session.Renames >= 20, $"renames {session.Renames}");
        Assert.True(localChanges >= 3, $"local changes {localChanges}");
        Assert.True(unchangedRosterFrames > Frames / 2, $"unchanged roster frames {unchangedRosterFrames}");
        Assert.True(rosterRebuilds >= 40, $"roster changes {rosterRebuilds}");
    }

    [Fact]
    public void RosterUnchangedBySnapshot_KeepsTheSameCachedList_AndRenameInCaseOnly_Resorts()
    {
        ClientScenePresenter presenter = new("local");
        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            "local",
            1,
            0,
            InitialWorld: null,
            Roster: [new PlayerRosterEntry("local", 1, "Me"), new PlayerRosterEntry("s-b", 2, "beta"), new PlayerRosterEntry("s-a", 3, "Beta")]));
        IReadOnlyList<PlayerRosterEntry> first = presenter.BuildPlayerRoster();
        PlayerRosterEntry[] firstCopy = first.ToArray();
        Assert.Equal(["local", "s-a", "s-b"], firstCopy.Select(entry => entry.SessionId));

        // Same players, same names, moving around: the cached order and list stay as they are.
        for (ulong tick = 1; tick <= 10; tick++)
        {
            presenter.ApplySnapshot(Snapshot(tick, ("local", 1, "Me"), ("s-b", 2, "beta"), ("s-a", 3, "Beta")));
            Assert.Same(first, presenter.BuildPlayerRoster());
            Assert.Equal(firstCopy, presenter.BuildPlayerRoster());
        }

        // A case-only rename ties the names, so the session id decides; it must re-sort.
        presenter.ApplySnapshot(Snapshot(11, ("local", 1, "Me"), ("s-b", 2, "Alpha"), ("s-a", 3, "beta")));
        Assert.Equal(["local", "s-b", "s-a"], presenter.BuildPlayerRoster().Select(entry => entry.SessionId));
        presenter.ApplySnapshot(Snapshot(12, ("local", 1, "Me"), ("s-b", 2, "BETA"), ("s-a", 3, "beta")));
        Assert.Equal(["local", "s-a", "s-b"], presenter.BuildPlayerRoster().Select(entry => entry.SessionId));

        // An actor id change alone (a rejoin under the same session) updates the entry.
        presenter.ApplySnapshot(Snapshot(13, ("local", 1, "Me"), ("s-b", 9, "BETA"), ("s-a", 3, "beta")));
        Assert.Equal(9, presenter.BuildPlayerRoster().Single(entry => entry.SessionId == "s-b").ActorId);
    }

    private static ServerSnapshotEnvelope Snapshot(ulong tick, params (string Session, int ActorId, string Name)[] players)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            players.Select(player => new ActorSnapshot(
                    player.ActorId,
                    CreatureKind.Player,
                    player.Session,
                    new Vector3(tick, 1.5f, player.ActorId),
                    -Vector3.UnitZ,
                    100,
                    100,
                    true,
                    DisplayName: player.Name))
                .ToArray(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private enum ScriptEvent
    {
        None,
        Join,
        Leave,
        Rename,
        LocalSessionChanged
    }

    // The server side of the script: who is connected, under which name and actor id.
    private sealed class ScriptedSession(Random random)
    {
        private static readonly string[] Names =
        [
            "alpha", "Alpha", "bravo", "BRAVO", "charlie", "delta", "Echo", "echo", "  ", "", "émile", "Emile", "zulu"
        ];

        private readonly List<Player> _players = [new("local-0", 1, "Me")];
        private int _nextActorId = 10;
        private int _nextSession = 1;

        public string LocalSessionId { get; private set; } = "local-0";

        public int Joins { get; private set; }

        public int Leaves { get; private set; }

        public int Renames { get; private set; }

        public ScriptEvent Step(int frame)
        {
            // Long quiet stretches: most snapshots change only positions and health.
            if (frame % 7 != 0)
            {
                return ScriptEvent.None;
            }

            int roll = random.Next(100);
            if (frame > 0 && frame % 399 == 0)
            {
                // The local player reconnects under a new session id and keeps its name.
                Player local = _players.Single(player => player.SessionId == LocalSessionId);
                _players.Remove(local);
                LocalSessionId = $"local-{frame}";
                _players.Add(new Player(LocalSessionId, _nextActorId++, local.Name));
                return ScriptEvent.LocalSessionChanged;
            }

            List<Player> remotes = _players.Where(player => player.SessionId != LocalSessionId).ToList();
            if (roll < 35 || remotes.Count == 0)
            {
                string session = random.Next(4) == 0 && _nextSession > 2
                    ? $"s{random.Next(1, _nextSession)}" // a former session id rejoins
                    : $"s{_nextSession++}";
                if (_players.Any(player => player.SessionId == session))
                {
                    return ScriptEvent.None;
                }

                _players.Add(new Player(session, _nextActorId++, Names[random.Next(Names.Length)]));
                Joins++;
                return ScriptEvent.Join;
            }

            if (roll < 65)
            {
                _players.Remove(remotes[random.Next(remotes.Count)]);
                Leaves++;
                return ScriptEvent.Leave;
            }

            Player renamed = remotes[random.Next(remotes.Count)];
            _players[_players.IndexOf(renamed)] = renamed with { Name = Names[random.Next(Names.Length)] };
            Renames++;
            return ScriptEvent.Rename;
        }

        public JoinAcceptedMessage CreateJoin(ulong serverTick)
        {
            Player local = _players.Single(player => player.SessionId == LocalSessionId);
            return new JoinAcceptedMessage(
                LocalSessionId,
                local.ActorId,
                serverTick,
                InitialWorld: null,
                Roster: _players.Select(player => new PlayerRosterEntry(player.SessionId, player.ActorId, player.Name)).ToArray(),
                InitialActors: CreateActors(serverTick));
        }

        public ServerSnapshotEnvelope CreateSnapshot(ulong tick)
        {
            return new ServerSnapshotEnvelope(
                tick,
                new Dictionary<string, ulong>(),
                CreateActors(tick),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>());
        }

        private ActorSnapshot[] CreateActors(ulong tick)
        {
            List<ActorSnapshot> actors = [];
            foreach (Player player in _players)
            {
                // Some players stand still for a while, others keep walking.
                float walk = player.ActorId % 3 == 0 ? 0f : tick * 0.05f;
                actors.Add(new ActorSnapshot(
                    player.ActorId,
                    CreatureKind.Player,
                    player.SessionId,
                    new Vector3(player.ActorId + walk, 1.5f, player.ActorId * 0.5f),
                    new Vector3(MathF.Sin(tick * 0.1f), 0f, MathF.Cos(tick * 0.1f)),
                    (int)(50 + (tick + (ulong)player.ActorId) % 50),
                    100,
                    (tick + (ulong)player.ActorId) % 97 != 0,
                    DisplayName: player.Name));
            }

            // Enemies are never players and never reach the roster.
            actors.Add(new ActorSnapshot(2000, CreatureKind.Enemy, null, new Vector3(1f, 1f, 1f), Vector3.UnitZ, 30, 30, true, DisplayName: "Wolf"));
            return actors.ToArray();
        }

        private sealed record Player(string SessionId, int ActorId, string Name);
    }

    // The presenter as it behaved before MEM-7: fresh collections on every call, LINQ for the
    // roster order (applied by the test), the same interpolation buffers.
    private sealed class ReferencePresenter(string localSessionId)
    {
        private readonly Dictionary<int, RemoteEntry> _remotePlayers = [];
        private readonly Dictionary<string, PlayerRosterEntry> _roster = new(StringComparer.Ordinal);

        public string LocalSessionId { get; private set; } = localSessionId;

        public IEnumerable<PlayerRosterEntry> Roster => _roster.Values;

        public void ApplyJoinAccepted(JoinAcceptedMessage joinAccepted)
        {
            if (!string.IsNullOrWhiteSpace(joinAccepted.SessionId))
            {
                LocalSessionId = joinAccepted.SessionId;
            }

            _remotePlayers.Clear();
            foreach (PlayerRosterEntry entry in joinAccepted.EffectiveRoster)
            {
                _roster[entry.SessionId] = entry with
                {
                    DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.SessionId : entry.DisplayName
                };
            }

            if (joinAccepted.EffectiveInitialActors.Count > 0)
            {
                ApplyActors(joinAccepted.CurrentServerTick, joinAccepted.EffectiveInitialActors);
            }
        }

        public void ApplySnapshot(ServerSnapshotEnvelope snapshot)
        {
            ApplyActors(snapshot.ServerTick, snapshot.Actors);
        }

        public RemotePlayerPresentationState[] BuildRemotePlayerStates(double renderTick)
        {
            List<RemotePlayerPresentationState> states = [];
            foreach ((int actorId, RemoteEntry entry) in _remotePlayers)
            {
                if (!entry.Buffer.TrySample(renderTick, out InterpolatedActorPose pose))
                {
                    continue;
                }

                states.Add(new RemotePlayerPresentationState(
                    actorId,
                    entry.SessionId,
                    entry.DisplayName,
                    pose,
                    entry.CurrentHealth,
                    entry.MaxHealth,
                    entry.IsAlive,
                    entry.IsMoving));
            }

            return states.ToArray();
        }

        private void ApplyActors(ulong serverTick, IReadOnlyList<ActorSnapshot> actors)
        {
            HashSet<int> observedRemoteActorIds = [];
            HashSet<string> observedSessions = new(StringComparer.Ordinal);
            foreach (ActorSnapshot actor in actors)
            {
                if (actor.Kind != CreatureKind.Player || string.IsNullOrWhiteSpace(actor.SessionId))
                {
                    continue;
                }

                observedSessions.Add(actor.SessionId);
                string displayName = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.SessionId : actor.DisplayName;
                _roster[actor.SessionId] = new PlayerRosterEntry(actor.SessionId, actor.ActorId, displayName);
                if (string.Equals(actor.SessionId, LocalSessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                observedRemoteActorIds.Add(actor.ActorId);
                if (!_remotePlayers.TryGetValue(actor.ActorId, out RemoteEntry? entry))
                {
                    entry = new RemoteEntry(actor.SessionId);
                    _remotePlayers[actor.ActorId] = entry;
                }

                entry.DisplayName = displayName;
                entry.CurrentHealth = actor.CurrentHealth;
                entry.MaxHealth = actor.MaxHealth;
                if (actor.IsAlive && !entry.IsAlive)
                {
                    // Wave I (I-D27): dead to alive is a respawn, so the avatar does not slide from where it died.
                    entry.Buffer.Clear();
                }

                entry.IsAlive = actor.IsAlive;
                entry.IsMoving = entry.LastPosition.HasValue && (actor.Position - entry.LastPosition.Value).LengthSquared > 0.0001f;
                entry.LastPosition = actor.Position;
                entry.Buffer.AddSample(new RemotePlayerSnapshotSample(actor.ActorId, actor.SessionId, serverTick, actor.Position, actor.Facing));
            }

            foreach ((int actorId, _) in _remotePlayers.ToArray())
            {
                if (!observedRemoteActorIds.Contains(actorId))
                {
                    _remotePlayers.Remove(actorId);
                }
            }

            foreach (string sessionId in _roster.Keys.ToArray())
            {
                if (!string.Equals(sessionId, LocalSessionId, StringComparison.Ordinal) && !observedSessions.Contains(sessionId))
                {
                    _roster.Remove(sessionId);
                }
            }
        }

        private sealed class RemoteEntry(string sessionId)
        {
            public string SessionId { get; } = sessionId;

            public string DisplayName { get; set; } = sessionId;

            public RemotePlayerInterpolationBuffer Buffer { get; } = new();

            public int CurrentHealth { get; set; }

            public int MaxHealth { get; set; }

            public bool IsAlive { get; set; }

            public bool IsMoving { get; set; }

            public Vector3? LastPosition { get; set; }
        }
    }
}
