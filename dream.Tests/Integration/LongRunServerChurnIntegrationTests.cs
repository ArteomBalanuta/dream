using System.Globalization;
using System.Reflection;
using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// MEM-3 (allocation-free runtime tick), MEM-9 (allocation-free map sync) and NET-7 (production
// SimulationStateChecksum) together on a long-running dedicated server: four sessions over 1,000
// ticks with session churn, combat with wolf deaths and respawns, building, several fractures and
// rubble settling. Two fresh servers run the same script in lockstep and must match on every tick;
// every tick the map invariants hold, the snapshot actor order is canonical, and a departed
// session is gone from the snapshot, the combat targets and the experience memo.
//
// Script (server tick numbers):
//   joins       a, b, c before tick 1 (a takes the primary seat);
//   build       a builds three cubes on ticks 3, 5 and 7;
//   fracture    a breaks the three cubes one after another from tick 60;
//   combat      b, c and d attack the nearest wolf from tick 30 to tick 900; the runtime
//               respawns the wolf every time it dies;
//   churn       c leaves on 200 (and, in the rejoin variant, rejoins on 260 on a new connection);
//               a, the primary seat holder, leaves on 400; nobody holds the seat until
//               d joins on 430 and takes it (in the rejoin variant a rejoins on 520 as a secondary);
//   build       d (now primary) builds a fourth cube on 440 and breaks it from 460;
//   settle      no attacks after 900, so the rubble comes to rest.
public class LongRunServerChurnIntegrationTests(ITestOutputHelper output)
{
    private const int Ticks = 1000;
    private const int FloorSize = 16;
    private const ulong Seed = 0x10_96_C4_0BUL;

    private const string SessionA = "session-a";
    private const string SessionB = "session-b";
    private const string SessionC = "session-c";
    private const string SessionD = "session-d";
    private static readonly string[] AllSessions = [SessionA, SessionB, SessionC, SessionD];

    private const int CombatFrom = 30;
    private const int CombatUntil = 900;
    private const int PrimaryCubeAttackFrom = 60;
    private const int CLeavesAt = 200;
    private const int CRejoinsAt = 260;
    private const int ALeavesAt = 400;
    private const int DJoinsAt = 430;
    private const int DBuildsAt = 440;
    private const int DCubeAttackFrom = 460;
    private const int ARejoinsAt = 520;
    private const int SettleCheckFrom = 960;

    // AllocationAssert sample points on a third server.
    private static readonly int[] AllocationSampleTicks = [150, 450, 700, 1000];

    // Documented budgets (WorldSessionRuntimeTickAllocationTests, DedicatedServerTickAllocationTests):
    // the runtime tick allocates only the player collision reports (64 B), and the runtime host layer
    // adds at most 128 B on top of it.
    private const long RuntimeTickBudgetBytes = 64;
    private const long RuntimeHostOverheadBudgetBytes = 128;
    private const long HostTickBudgetBytes = RuntimeTickBudgetBytes + RuntimeHostOverheadBudgetBytes;

    private static readonly Vector3[] PrimaryCubeCells = [new(7f, 1f, 5f), new(7f, 1f, 3f), new(7f, 1f, 7f)];
    private static readonly int[] PrimaryBuildTicks = [3, 5, 7];
    private static readonly Vector3 SecondPrimaryCubeCell = new(3f, 1f, 5f);

    // c and a leave and stay away; d joins after a has left and takes the free primary seat.
    [Fact]
    public void FourSessions_ThousandTicks_ChurnCombatBuildFracture_TwoServersMatchEveryTick_AndInvariantsHold()
    {
        RunLockstepScript(withRejoins: false);
    }

    // The full script: c rejoins on 260 and a, the former primary, rejoins on 520 after d took the seat.
    // A rejoin goes through RuntimeAuthoritativeSessionHost.JoinSession's existing-session branch,
    // which attaches the session to the runtime again; see SessionRejoinIntegrationTests.
    [Fact]
    public void FourSessions_ThousandTicks_WithRejoins_TwoServersMatchEveryTick_AndInvariantsHold()
    {
        RunLockstepScript(withRejoins: true);
    }

    private void RunLockstepScript(bool withRejoins)
    {
        using ChurnServer first = new(Seed);
        using ChurnServer second = new(Seed);
        Assert.Equal(first.Checksum(), second.Checksum());

        int firstDivergence = -1;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            ScriptStep step = Script(tick, first, withRejoins);
            first.Advance(tick, step);
            second.Advance(tick, step);

            Assert.Equal(first.LastSnapshotOrder, second.LastSnapshotOrder);
            if (firstDivergence < 0 && first.Checksum() != second.Checksum())
            {
                firstDivergence = tick;
                output.WriteLine($"tick {tick}: {first.Diff(second)}");
            }
        }

        ChurnOutcome outcome = first.Outcome;
        output.WriteLine(outcome.ToString());
        Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence}: {first.Diff(second)}");
        Assert.Equal(outcome, second.Outcome);

        // The script exercised everything it claims to.
        Assert.True(outcome.WolfDeaths >= 2, $"Only {outcome.WolfDeaths} wolf deaths; the respawn path was not exercised.");
        Assert.True(outcome.BuiltCubes >= 4, $"Only {outcome.BuiltCubes} cubes were built.");
        Assert.True(outcome.Fractures >= 3, $"Only {outcome.Fractures} cubes fractured.");
        Assert.True(outcome.RubblePieces > 0, "No rubble pieces.");
        Assert.True(outcome.TicksWithoutSeatHolder > 0, "The primary seat was never empty.");
        Assert.Equal(SessionD, first.PrimarySession);
        Assert.True(outcome.SettledRubbleTicks >= Ticks - SettleCheckFrom, $"Rubble still moving: settled for {outcome.SettledRubbleTicks} ticks.");
    }

    [Fact]
    public void FourSessions_ThousandTicks_ChurnCombatBuildFracture_TickAllocationStaysWithinBudget()
    {
        using ChurnServer server = new(Seed);
        List<string> samples = [];
        for (int tick = 1; tick <= Ticks; tick++)
        {
            server.Advance(tick, Script(tick, server, withRejoins: false));
            if (Array.IndexOf(AllocationSampleTicks, tick) < 0)
            {
                continue;
            }

            // A host tick as the dedicated server runs it between client inputs: the full runtime
            // tick (wolf AI, player physics, rubble sync) through DedicatedServerHost.AdvanceTime.
            // The sample advances the server; the script then continues on that state.
            long bytes = AllocationAssert.MeasureBytesPerIteration(
                () => server.AdvanceOneIdleTick(),
                iterations: 20);
            samples.Add($"tick {tick}: {bytes} B ({server.DescribeLoad()})");
            Assert.True(
                bytes <= HostTickBudgetBytes,
                $"At tick {tick} ({server.DescribeLoad()}) a host tick allocated {bytes} B; budget is {HostTickBudgetBytes} B.");
        }

        output.WriteLine(string.Join(Environment.NewLine, samples));
        Assert.Equal(AllocationSampleTicks.Length, samples.Count);
    }

    // Commands for every present session; actions for the scripted build ticks. Aim comes from the
    // reference server's state, so both servers receive the identical stream.
    private static ScriptStep Script(int tick, ChurnServer reference, bool withRejoins)
    {
        Dictionary<string, PlayerCommandFrame> commands = new(StringComparer.Ordinal);
        List<(string Session, IPlayerActionPayload Payload)> actions = [];
        List<(string Session, bool Join)> churn = [];

        switch (tick)
        {
            case CLeavesAt: churn.Add((SessionC, false)); break;
            case CRejoinsAt when withRejoins: churn.Add((SessionC, true)); break;
            case ALeavesAt: churn.Add((SessionA, false)); break;
            case DJoinsAt: churn.Add((SessionD, true)); break;
            case ARejoinsAt when withRejoins: churn.Add((SessionA, true)); break;
        }

        bool fighting = tick >= CombatFrom && tick < CombatUntil;
        foreach (string session in AllSessions)
        {
            if (!reference.IsPresentAfter(session, churn))
            {
                continue;
            }

            bool isPrimary = reference.IsPrimaryAfter(session, churn);
            Vector3 facing = Vector3.UnitZ;
            bool attack = false;
            PlayerInteractionMode mode = PlayerInteractionMode.Building;
            // A session joining this tick has no state to aim from yet.
            bool joiningNow = !reference.IsListed(session);
            bool primaryBreaking = !joiningNow && isPrimary && fighting && (session == SessionA
                ? tick >= PrimaryCubeAttackFrom
                : tick >= DCubeAttackFrom);
            if (primaryBreaking && reference.TryGetNextCubeTarget(session, out Vector3 cubeTarget))
            {
                facing = DirectionFrom(reference.EyeOf(session), cubeTarget);
                attack = true;
                mode = PlayerInteractionMode.Combat;
            }
            else if (!joiningNow && !isPrimary && fighting && reference.TryGetNearestWolfAnchor(session, out Vector3 wolf))
            {
                facing = DirectionFrom(reference.EyeOf(session), wolf);
                attack = true;
                mode = PlayerInteractionMode.Combat;
            }

            commands[session] = new PlayerCommandFrame(
                new PlayerSessionId(session),
                (ulong)tick,
                new PlayerModeSwitchIntent(mode),
                new PlayerMoveIntent(0f, 0f),
                new PlayerLookIntent(0f, 0f, facing),
                PrimaryUsePressed: attack);
        }

        int buildIndex = Array.IndexOf(PrimaryBuildTicks, tick);
        if (buildIndex >= 0)
        {
            actions.Add((SessionA, new BuildUseIntent(PrimaryCubeCells[buildIndex])));
        }

        if (tick == DBuildsAt)
        {
            actions.Add((SessionD, new BuildUseIntent(SecondPrimaryCubeCell)));
        }

        return new ScriptStep(churn, commands, actions);
    }

    private static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? Vector3.UnitZ : direction.Normalized();
    }

    private sealed record ScriptStep(
        IReadOnlyList<(string Session, bool Join)> Churn,
        IReadOnlyDictionary<string, PlayerCommandFrame> Commands,
        IReadOnlyList<(string Session, IPlayerActionPayload Payload)> Actions);

    private sealed record ChurnOutcome(
        int WolfDeaths,
        int BuiltCubes,
        int Fractures,
        int RubblePieces,
        int TicksWithoutSeatHolder,
        int SettledRubbleTicks,
        int Hits);

    private sealed class ChurnServer : IDisposable
    {
        private static readonly FieldInfo ExperienceMemoField = typeof(WorldSessionRuntime).GetField(
            "_experienceToNextLevelByActor",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WorldSessionRuntime._experienceToNextLevelByActor not found.");

        private readonly SceneEventHub _events = new();
        private readonly WorldSessionRuntime _runtime;
        private readonly DedicatedServerHost _host;
        private readonly Dictionary<string, string> _connectionBySession = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _connectionGeneration = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ulong> _actionSequences = new(StringComparer.Ordinal);
        // Actor ids a session held when it left, while it is away.
        private readonly Dictionary<string, int> _departedActorIds = new(StringComparer.Ordinal);
        private readonly List<IDisposable> _subscriptions = [];
        private readonly List<AuthoritativeActionResult> _actionResults = [];
        private int _wolfDeaths;
        private int _builtCubes;
        private int _fractures;
        private int _hits;
        private int _ticksWithoutSeatHolder;
        private int _settledRubbleTicks;
        private ulong _previousRubble;
        private int _tick;
        private SimulationStateChecksum.Parts _parts;
        private IReadOnlyList<ActorSnapshot> _lastActors = [];

        public ChurnServer(ulong seed)
        {
            _runtime = new WorldSessionRuntime(
                FloorSize,
                new Vector3(5f, 1.5f, 5f),
                _events,
                loadVisualAssets: false,
                simulationSeed: seed);
            ProtocolRouter router = new(
                [
                    new ClientHelloHandler(),
                    new JoinRequestHandler(),
                    new PlayerCommandHandler(),
                    new PlayerActionHandler()
                ],
                Array.Empty<IClientMessageHandler>());
            _host = new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(_runtime), router);
            _subscriptions.Add(_events.Subscribe<DamageAppliedEvent>(_ => _hits++));
            _subscriptions.Add(_events.Subscribe<CubeFracturedEvent>(_ => _fractures++));

            Join(SessionA);
            Join(SessionB);
            Join(SessionC);
            Assert.Equal(SessionA, PrimarySession);
            CaptureChecksum();
        }

        public string? PrimarySession => _runtime.PrimaryRemoteSessionId?.Value;

        public IReadOnlyList<string> LastSnapshotOrder { get; private set; } = [];

        public ChurnOutcome Outcome => new(
            _wolfDeaths,
            _builtCubes,
            _fractures,
            _runtime.Map.CaptureFracturePieces().Count(),
            _ticksWithoutSeatHolder,
            _settledRubbleTicks,
            _hits);

        public bool IsPresentAfter(string session, IReadOnlyList<(string Session, bool Join)> churn)
        {
            foreach ((string churned, bool join) in churn)
            {
                if (churned == session)
                {
                    return join;
                }
            }

            return _connectionBySession.ContainsKey(session);
        }

        // Who holds the seat once this tick's churn is applied: a leaving holder frees it, and a
        // session that has never joined takes a free seat.
        public bool IsPrimaryAfter(string session, IReadOnlyList<(string Session, bool Join)> churn)
        {
            string? primary = PrimarySession;
            foreach ((string churned, bool join) in churn)
            {
                if (!join && churned == primary)
                {
                    primary = null;
                }
                else if (join && primary == null && !_connectionGeneration.ContainsKey(churned))
                {
                    primary = churned;
                }
            }

            return primary == session;
        }

        public bool IsListed(string session) => _lastActors.Any(actor => actor.SessionId == session);

        public Vector3 EyeOf(string session)
        {
            ActorSnapshot actor = _lastActors.Single(actor => actor.SessionId == session);
            return actor.Position + new Vector3(0f, 0.35f, 0f);
        }

        public bool TryGetNearestWolfAnchor(string session, out Vector3 anchor)
        {
            Vector3 eye = EyeOf(session);
            EnemyActor? nearest = _runtime.Enemies.ActiveActors
                .Where(enemy => enemy.Combatant.Health.IsAlive)
                .OrderBy(enemy => (enemy.Position - eye).LengthSquared)
                .FirstOrDefault();
            anchor = nearest?.Combatant.GetCombatAnchorWorldPosition() ?? default;
            return nearest != null;
        }

        // The first scripted cube of this primary that is still standing.
        public bool TryGetNextCubeTarget(string session, out Vector3 target)
        {
            IEnumerable<Vector3> cells = session == SessionA ? PrimaryCubeCells : [SecondPrimaryCubeCell];
            foreach (Vector3 cell in cells)
            {
                if (_runtime.Map.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z) is Cube cube)
                {
                    target = cube.Position;
                    return true;
                }
            }

            target = default;
            return false;
        }

        public void Advance(int tick, ScriptStep step)
        {
            _tick = tick;
            foreach ((string session, bool join) in step.Churn)
            {
                if (join)
                {
                    Join(session);
                }
                else
                {
                    Leave(session);
                }
            }

            foreach (string session in step.Commands.Keys.Order(StringComparer.Ordinal))
            {
                _host.HandleClientMessage(_connectionBySession[session], new PlayerCommandMessage(step.Commands[session]));
            }

            foreach ((string session, IPlayerActionPayload payload) in step.Actions)
            {
                ulong sequence = _actionSequences.TryGetValue(session, out ulong last) ? last + 1 : 1;
                _actionSequences[session] = sequence;
                _host.HandleClientMessage(
                    _connectionBySession[session],
                    new PlayerActionMessage(new PlayerSessionId(session), sequence, payload));
            }

            Assert.Equal(1, AdvanceOneTick());
            _host.DrainPendingActionResultsInto(_actionResults);
            ServerSnapshotEnvelope snapshot = _host.CreateSnapshotMessage().Snapshot;
            foreach (WorldMutationDelta mutation in snapshot.WorldMutations)
            {
                if (mutation.Kind == WorldMutationKind.CubePlaced)
                {
                    _builtCubes++;
                }
            }

            foreach (DamageEventDelta damage in snapshot.DamageEvents)
            {
                if (damage.Kind == CreatureKind.Enemy && damage.ActorDied)
                {
                    _wolfDeaths++;
                }
            }

            if (PrimarySession == null)
            {
                _ticksWithoutSeatHolder++;
            }

            CaptureChecksum();
            AssertTickInvariants(snapshot);
        }

        // One tick with no client input, for the allocation samples.
        public void AdvanceOneIdleTick()
        {
            AdvanceOneTick();
        }

        public string DescribeLoad()
        {
            return $"primary={PrimarySession ?? "<none>"} players={_lastActors.Count(actor => actor.Kind == CreatureKind.Player)} " +
                $"enemies={_runtime.Enemies.ActiveActors.Count} rubble pieces={_runtime.Map.CaptureFracturePieces().Count()} " +
                $"wolf={_runtime.Enemies.ActiveActors.FirstOrDefault()?.BehaviorState}";
        }

        public ulong Checksum() => _parts.Combined;

        public string Diff(ChurnServer other)
        {
            StringBuilder diff = new();
            AppendPart(diff, "world", _parts.World, other._parts.World);
            AppendPart(diff, "actors", _parts.Actors, other._parts.Actors);
            AppendPart(diff, "rubble", _parts.Rubble, other._parts.Rubble);
            AppendPart(diff, "mutation sequence", _parts.WorldMutationSequence, other._parts.WorldMutationSequence);
            foreach ((ActorSnapshot mine, ActorSnapshot theirs) in _lastActors.Zip(other._lastActors))
            {
                if (SimulationStateChecksum.HashActor(mine) != SimulationStateChecksum.HashActor(theirs))
                {
                    diff.Append(" [").Append(mine).Append(" vs ").Append(theirs).Append(']');
                }
            }

            return diff.ToString();
        }

        public void Dispose()
        {
            foreach (IDisposable subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _host.Dispose();
        }

        private void Join(string session)
        {
            int generation = _connectionGeneration.TryGetValue(session, out int previous) ? previous + 1 : 0;
            _connectionGeneration[session] = generation;
            string connection = $"{session}-connection-{generation.ToString(CultureInfo.InvariantCulture)}";
            _connectionBySession[session] = connection;
            IReadOnlyList<IServerToClientMessage> replies = _host.HandleClientMessage(
                connection,
                new ClientJoinRequestMessage(session, $"Name {session}"));
            JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies));
            Assert.Equal(session, accepted.SessionId);
            _departedActorIds.Remove(session);
        }

        private void Leave(string session)
        {
            ActorSnapshot actor = _lastActors.Single(actor => actor.SessionId == session);
            _departedActorIds[session] = actor.ActorId;
            _host.ReleaseConnection(_connectionBySession[session]);
            _connectionBySession.Remove(session);
        }

        private int AdvanceOneTick()
        {
            int executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            if (executed == 0)
            {
                executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
            }

            return executed;
        }

        private void CaptureChecksum()
        {
            _lastActors = _runtime.CaptureActorSnapshots();
            _parts = SimulationStateChecksum.ComputeParts(_runtime.Map, _lastActors, _runtime.LastWorldMutationSequence);
            if (_tick >= SettleCheckFrom && _parts.Rubble == _previousRubble)
            {
                _settledRubbleTicks++;
            }

            _previousRubble = _parts.Rubble;
        }

        private void AssertTickInvariants(ServerSnapshotEnvelope snapshot)
        {
            IReadOnlyList<string> violations = _runtime.Map.FindInvariantViolations();
            Assert.True(violations.Count == 0, $"tick {_tick}: map invariants violated: {string.Join("; ", violations)}");

            AssertCanonicalOrder(snapshot.Actors, "snapshot");
            AssertCanonicalOrder(_lastActors, "CaptureActorSnapshots");
            LastSnapshotOrder = snapshot.Actors.Select(actor => actor.SessionId ?? "enemy:" + actor.ActorId).ToArray();

            // Every present session is listed exactly once.
            foreach (string session in _connectionBySession.Keys)
            {
                int listed = snapshot.Actors.Count(actor => actor.SessionId == session);
                Assert.True(
                    listed == 1,
                    $"tick {_tick}: present {session} is listed {listed} times in [{string.Join(", ", snapshot.Actors)}]; primary={PrimarySession}.");
            }

            Dictionary<int, (int Level, int Experience, int Required)> memo =
                (Dictionary<int, (int Level, int Experience, int Required)>)ExperienceMemoField.GetValue(_runtime)!;
            int listedPlayers = snapshot.Actors.Count(actor => actor.Kind == CreatureKind.Player);
            foreach ((string session, int actorId) in _departedActorIds)
            {
                Assert.False(
                    snapshot.Actors.Any(actor => actor.SessionId == session) || _lastActors.Any(actor => actor.SessionId == session),
                    $"tick {_tick}: departed {session} is still in the snapshot.");

                // The primary actor id is the seat's: once another session takes the seat it is
                // legitimately listed, targeted and damaged again, under that session.
                ActorSnapshot? holder = snapshot.Actors
                    .Where(actor => actor.ActorId == actorId)
                    .Select(actor => (ActorSnapshot?)actor)
                    .FirstOrDefault();
                if (holder.HasValue)
                {
                    Assert.True(
                        actorId == _runtime.Player.Combatant.Actor.Id && holder.Value.SessionId == PrimarySession,
                        $"tick {_tick}: departed {session}'s actor {actorId} is listed as {holder.Value}.");
                }
                else
                {
                    Assert.False(
                        snapshot.DamageEvents.Any(damage => damage.ActorId == actorId),
                        $"tick {_tick}: departed {session} (actor {actorId}) took damage: [{string.Join(", ", snapshot.DamageEvents)}].");
                    Assert.False(
                        _runtime.TryResolveCombatActor(actorId, out _),
                        $"tick {_tick}: departed {session} (actor {actorId}) still resolves as a combat target.");
                }

                // The primary actor id belongs to the seat, not the session: its memo entry is reused by
                // the next seat holder. A secondary's actor id is the departed session's own.
                if (actorId != _runtime.Player.Combatant.Actor.Id)
                {
                    Assert.False(
                        memo.ContainsKey(actorId),
                        $"tick {_tick}: departed {session} (actor {actorId}) left an experience-memo entry.");
                }
            }

            // The memo never outgrows the players listed, plus the primary seat's actor id.
            Assert.True(
                memo.Count <= listedPlayers + 1,
                $"tick {_tick}: experience memo has {memo.Count} entries for {listedPlayers} listed players.");
        }

        // The primary seat first (when a remote session holds it), then the secondaries in ordinal
        // session-id order, then the enemies.
        private void AssertCanonicalOrder(IReadOnlyList<ActorSnapshot> actors, string source)
        {
            int index = 0;
            string? primary = PrimarySession;
            if (primary != null)
            {
                Assert.True(actors.Count > 0, $"tick {_tick}: {source} is empty.");
                Assert.True(
                    actors[0].Kind == CreatureKind.Player && actors[0].SessionId == primary && actors[0].ActorId == _runtime.Player.Combatant.Actor.Id,
                    $"tick {_tick}: {source} starts with {actors[0]}, not the primary {primary}.");
                index = 1;
            }

            string? previous = null;
            for (; index < actors.Count && actors[index].Kind == CreatureKind.Player; index++)
            {
                string session = actors[index].SessionId!;
                Assert.NotEqual(primary, session);
                Assert.True(
                    previous == null || string.CompareOrdinal(previous, session) < 0,
                    $"tick {_tick}: {source} lists secondary {session} after {previous}.");
                previous = session;
            }

            for (; index < actors.Count; index++)
            {
                Assert.True(
                    actors[index].Kind == CreatureKind.Enemy,
                    $"tick {_tick}: {source} lists {actors[index]} after the enemies started.");
            }
        }

        private static void AppendPart(StringBuilder diff, string name, ulong left, ulong right)
        {
            if (left != right)
            {
                diff.Append(" [").Append(name).Append(' ').Append(left).Append(" vs ").Append(right).Append(']');
            }
        }
    }
}
