using System.Globalization;
using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// NET-1/6 with NET-2: two independent servers (DedicatedServerHost over a runtime-backed
// authority), the same simulation seed and the same scripted command and action stream for 300
// ticks: three players walk, all of them fight an enemy until it dies (after which the runtime's
// own wolf spawns and fights back), and the primary breaks a cube it built until it fractures
// into rubble. After every tick an order-independent hash of the authoritative state is
// compared: every actor (position quantized to 1e-4, health, alive), the world checksum and
// every rubble piece.
public class TwoServerDeterminismIntegrationTests(ITestOutputHelper output)
{
    private const int Ticks = 300;
    private const int FloorSize = 16;
    private const ulong Seed = 0xD7EA_5EEDUL;
    private const int EnemyActorId = 4300;
    // Several hits from two attackers, so the kill depends on every roll.
    private const int EnemyMaxHealth = 400;
    private const string PrimarySession = "session-host";
    private const string SessionB = "session-b";
    private const string SessionC = "session-c";

    // Ticks of the script.
    private const int WalkUntil = 20;
    private const int BuildAt = 3;
    private const int FightFrom = 30;
    private const int CubeAttackFrom = 150;

    private static readonly Vector3 CubeCell = new(7f, 1f, 5f);
    private static readonly Vector3 EnemyPosition = new(7.5f, 1f, 11f);

    [Fact]
    public void SameSeed_SameScript_TwoServers_StayIdenticalEveryTick_ThroughCombatAndFracture()
    {
        using ScriptedServer first = new(Seed, [PrimarySession, SessionB, SessionC], reverseCommandDelivery: false);
        using ScriptedServer second = new(Seed, [PrimarySession, SessionB, SessionC], reverseCommandDelivery: true);

        RunLockstep(first, second, out int firstDivergence, out ScriptOutcome outcome);

        Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence}: {first.LastDiff(second)}");
        AssertScriptExercisedEverything(outcome);
    }

    // The secondaries join in the opposite order on the second server. Every per-tick iteration
    // is canonical now (NET-6), but a secondary's spawn point is still its join index:
    // WorldSessionRuntime.CreateSecondaryPlayer (WorldSessionRuntime.cs:222) passes
    // _secondaryRemotePlayers.Count to ResolveSecondaryRemoteSpawnPosition (line 2154), whose lane
    // offset is 2 + index. session-b spawns at x+2 on one server and x+3 on the other, so the
    // state differs from tick 0. Secondary actor ids are also allocated in join order
    // (AllocateRemoteActorId, line 2149), which is why players are keyed by session id here.
    [Fact(Skip = "Join-order dependent spawn: WorldSessionRuntime.CreateSecondaryPlayer (WorldSessionRuntime.cs:222) spawns a secondary at lane 2 + its join index (ResolveSecondaryRemoteSpawnPosition, WorldSessionRuntime.cs:2154), so the same sessions joining in a different order start at different positions.")]
    public void SameSeed_SecondariesJoinInDifferentOrders_TwoServers_StayIdenticalEveryTick()
    {
        using ScriptedServer first = new(Seed, [PrimarySession, SessionB, SessionC], reverseCommandDelivery: false);
        using ScriptedServer second = new(Seed, [PrimarySession, SessionC, SessionB], reverseCommandDelivery: true);

        RunLockstep(first, second, out int firstDivergence, out ScriptOutcome outcome);

        Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence}: {first.LastDiff(second)}");
        AssertScriptExercisedEverything(outcome);
    }

    [Fact]
    public void DifferentSeeds_SameScript_MatchUntilTheFirstHit_ThenDiverge()
    {
        using ScriptedServer first = new(Seed, [PrimarySession, SessionB, SessionC], reverseCommandDelivery: false);
        using ScriptedServer second = new(Seed + 1, [PrimarySession, SessionB, SessionC], reverseCommandDelivery: false);

        RunLockstep(first, second, out int firstDivergence, out ScriptOutcome outcome);

        // Walking, building and everything else before combat is seed independent; the first
        // damage roll is not.
        Assert.True(outcome.FirstHitTick > 0, "No hit landed.");
        Assert.True(firstDivergence >= FightFrom, $"The servers diverged at tick {firstDivergence}, before combat started.");
        Assert.True(firstDivergence >= outcome.FirstHitTick, $"Diverged at {firstDivergence}, before the first hit at {outcome.FirstHitTick}.");
        Assert.True(firstDivergence > 0, "Different seeds never diverged.");
        output.WriteLine($"different seeds: first hit tick {outcome.FirstHitTick}, first divergence tick {firstDivergence}");
    }

    private void RunLockstep(ScriptedServer first, ScriptedServer second, out int firstDivergence, out ScriptOutcome outcome)
    {
        firstDivergence = -1;
        Assert.Equal(first.Hash(), second.Hash());
        for (int tick = 1; tick <= Ticks; tick++)
        {
            ScriptStep step = Script(tick, first);
            first.Advance(tick, step);
            second.Advance(tick, step);
            if (firstDivergence < 0 && first.Hash() != second.Hash())
            {
                firstDivergence = tick;
                output.WriteLine($"tick {tick}: {first.LastDiff(second)}");
            }
        }

        outcome = first.Outcome;
        output.WriteLine(
            $"enemy died at tick {outcome.EnemyDeathTick}, cube fractured at tick {outcome.FractureTick}, " +
            $"first hit at {outcome.FirstHitTick}, rubble pieces {outcome.RubblePieces}, hits {outcome.Hits}, " +
            $"player damage taken {outcome.PlayerDamageTaken}");
    }

    private static void AssertScriptExercisedEverything(ScriptOutcome outcome)
    {
        Assert.True(outcome.EnemyDeathTick > 0, "The scripted enemy never died.");
        Assert.True(outcome.FractureTick > 0, "The cube never fractured.");
        Assert.True(outcome.RubblePieces > 0, "No rubble pieces.");
    }

    // The script is computed from the first server's state and fed to both, so the input stream
    // is identical by construction. Aim directions come from that state.
    private static ScriptStep Script(int tick, ScriptedServer reference)
    {
        Dictionary<string, PlayerCommandFrame> commands = new(StringComparer.Ordinal);
        List<(string Session, IPlayerActionPayload Payload)> actions = [];
        bool walking = tick <= WalkUntil;
        bool fighting = tick >= FightFrom;
        Vector3? enemyAnchor = reference.TryGetScriptedEnemyAnchor();

        foreach (string session in new[] { PrimarySession, SessionB, SessionC })
        {
            bool isPrimary = session == PrimarySession;
            Vector3 facing = Vector3.UnitZ;
            bool attack = false;
            if (isPrimary && tick >= CubeAttackFrom)
            {
                facing = Vector3.UnitX;
                attack = true;
            }
            else if (!isPrimary && fighting)
            {
                if (enemyAnchor.HasValue)
                {
                    facing = DirectionFrom(reference.EyeOf(session), enemyAnchor.Value);
                    attack = true;
                }
                else if (reference.TryGetNearestEnemyAnchor(session, out Vector3 wolf))
                {
                    // The scripted enemy is dead; fight the wolf the runtime spawns instead.
                    facing = DirectionFrom(reference.EyeOf(session), wolf);
                    attack = true;
                }
            }

            commands[session] = new PlayerCommandFrame(
                new PlayerSessionId(session),
                (ulong)tick,
                new PlayerModeSwitchIntent(fighting || (isPrimary && tick >= CubeAttackFrom)
                    ? PlayerInteractionMode.Combat
                    : PlayerInteractionMode.Building),
                new PlayerMoveIntent(walking && !isPrimary ? 1f : 0f, walking && isPrimary ? 0.5f : 0f),
                new PlayerLookIntent(0f, 0f, facing),
                PrimaryUsePressed: attack);
        }

        if (tick == BuildAt)
        {
            actions.Add((PrimarySession, new BuildUseIntent(CubeCell)));
        }

        return new ScriptStep(commands, actions);
    }

    private static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? Vector3.UnitZ : direction.Normalized();
    }

    private sealed record ScriptStep(
        IReadOnlyDictionary<string, PlayerCommandFrame> Commands,
        IReadOnlyList<(string Session, IPlayerActionPayload Payload)> Actions);

    private sealed record ScriptOutcome(int EnemyDeathTick, int FractureTick, int FirstHitTick, int RubblePieces, int Hits, int PlayerDamageTaken);

    private sealed class ScriptedServer : IDisposable
    {
        private readonly SceneEventHub _events = new();
        private readonly WorldSessionRuntime _runtime;
        private readonly DedicatedServerHost _host;
        private readonly bool _reverseCommandDelivery;
        private readonly Dictionary<string, string> _connectionBySession = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ulong> _actionSequences = new(StringComparer.Ordinal);
        private readonly List<IDisposable> _subscriptions = [];
        private readonly EnemyActor _scriptedEnemy;
        private int _enemyDeathTick;
        private int _fractureTick;
        private int _firstHitTick;
        private int _hits;
        private int _playerDamageTaken;
        private int _currentTick;
        private string _lastHash = string.Empty;

        public ScriptedServer(ulong seed, string[] joinOrder, bool reverseCommandDelivery)
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
            _reverseCommandDelivery = reverseCommandDelivery;

            // Connection ids differ between the servers too, so ConcurrentDictionary order does.
            for (int i = 0; i < joinOrder.Length; i++)
            {
                string connection = reverseCommandDelivery ? $"conn-{joinOrder.Length - i}" : $"connection-{i}";
                _connectionBySession[joinOrder[i]] = connection;
                IReadOnlyList<IServerToClientMessage> replies = _host.HandleClientMessage(
                    connection,
                    new ClientJoinRequestMessage(joinOrder[i], $"Name {joinOrder[i]}"));
                Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies));
            }

            Assert.Equal(PrimarySession, _runtime.PrimaryRemoteSessionId?.Value);

            _runtime.Enemies.Clear();
            EnemyCombatant combatant = new(
                () => EnemyPosition,
                _events,
                actorId: EnemyActorId,
                maxHealth: EnemyMaxHealth,
                attackDamage: 2,
                attackIntervalSeconds: 0.5f);
            // Stands still but fights back at whoever is in reach.
            _scriptedEnemy = new EnemyActor(
                combatant,
                EnemyPosition,
                aggroRange: 8f,
                attackRange: 3f,
                moveSpeed: 0f,
                displayName: "Scripted wolf");
            _runtime.Enemies.Add(_scriptedEnemy);

            _subscriptions.Add(_events.Subscribe<DamageAppliedEvent>(_ => _hits++));
            _subscriptions.Add(_events.Subscribe<CubeFracturedEvent>(_ => _fractureTick = _fractureTick == 0 ? _currentTick : _fractureTick));
        }

        public ScriptOutcome Outcome => new(
            _enemyDeathTick,
            _fractureTick,
            _firstHitTick,
            _runtime.Map.CaptureFracturePieces().Count(),
            _hits,
            _playerDamageTaken);

        public Vector3? TryGetScriptedEnemyAnchor()
        {
            return _scriptedEnemy.Combatant.Health.IsAlive
                ? _scriptedEnemy.Combatant.GetCombatAnchorWorldPosition()
                : null;
        }

        public bool TryGetNearestEnemyAnchor(string session, out Vector3 anchor)
        {
            Vector3 eye = EyeOf(session);
            EnemyActor? nearest = _runtime.Enemies.ActiveActors
                .Where(enemy => enemy.Combatant.Health.IsAlive)
                .OrderBy(enemy => (enemy.Position - eye).LengthSquared)
                .FirstOrDefault();
            anchor = nearest?.Combatant.GetCombatAnchorWorldPosition() ?? default;
            return nearest != null;
        }

        public Vector3 EyeOf(string session)
        {
            ActorSnapshot actor = _runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == session);
            return actor.Position + new Vector3(0f, 0.35f, 0f);
        }

        public void Advance(int tick, ScriptStep step)
        {
            _currentTick = tick;
            int healthBefore = PlayerHealthTotal();
            IEnumerable<string> sessions = _reverseCommandDelivery
                ? step.Commands.Keys.Order(StringComparer.Ordinal).Reverse()
                : step.Commands.Keys.Order(StringComparer.Ordinal);
            foreach (string session in sessions)
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

            int executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            if (executed == 0)
            {
                executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
            }

            Assert.Equal(1, executed);
            Assert.Equal((ulong)tick, _runtime.CurrentServerTick);
            _host.DrainPendingActionResults();
            // Deltas are exported every tick, like the service does for joined connections.
            _ = _host.CreateSnapshotMessage();
            // The first seeded roll: a player's hit on the scripted enemy (the enemy's own hits on
            // players are not rolled).
            if (_firstHitTick == 0 && _scriptedEnemy.Combatant.Health.CurrentHealth < EnemyMaxHealth)
            {
                _firstHitTick = tick;
            }

            if (_enemyDeathTick == 0 && !_scriptedEnemy.Combatant.Health.IsAlive)
            {
                _enemyDeathTick = tick;
            }

            _playerDamageTaken += Math.Max(0, healthBefore - PlayerHealthTotal());
            _lastHash = ComputeHash();
        }

        public string Hash() => _lastHash.Length == 0 ? _lastHash = ComputeHash() : _lastHash;

        public string LastDiff(ScriptedServer other)
        {
            string[] mine = Hash().Split('\n');
            string[] theirs = other.Hash().Split('\n');
            StringBuilder diff = new();
            for (int i = 0; i < Math.Max(mine.Length, theirs.Length); i++)
            {
                string left = i < mine.Length ? mine[i] : "<none>";
                string right = i < theirs.Length ? theirs[i] : "<none>";
                if (left != right)
                {
                    diff.Append(" [").Append(left).Append(" vs ").Append(right).Append(']');
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

        private int PlayerHealthTotal()
        {
            return _runtime.CaptureActorSnapshots()
                .Where(actor => actor.Kind == CreatureKind.Player)
                .Sum(actor => actor.CurrentHealth);
        }

        // Order independent: actors sorted by key, rubble sorted by cell and piece index.
        // Players are keyed by session id, because secondary actor ids are allocated in join order.
        private string ComputeHash()
        {
            StringBuilder hash = new();
            IEnumerable<string> actors = _runtime.CaptureActorSnapshots()
                .Select(actor =>
                    $"{(actor.Kind == CreatureKind.Player ? "player:" + actor.SessionId : "enemy:" + actor.ActorId.ToString(CultureInfo.InvariantCulture))} " +
                    $"pos={Quantize(actor.Position)} hp={actor.CurrentHealth}/{actor.MaxHealth} alive={actor.IsAlive}")
                .Order(StringComparer.Ordinal);
            foreach (string actor in actors)
            {
                hash.Append(actor).Append('\n');
            }

            hash.Append("world=").Append(WorldStateChecksum.Compute(_runtime.Map).ToString(CultureInfo.InvariantCulture)).Append('\n');
            IEnumerable<string> rubble = _runtime.Map.CaptureFracturePieces()
                .Select(piece => $"rubble {piece.Cell} #{piece.PieceIndex} {Quantize(piece.Piece.Center)} side={piece.Piece.SideLength.ToString("R", CultureInfo.InvariantCulture)} {piece.MaterialId}")
                .Order(StringComparer.Ordinal);
            foreach (string piece in rubble)
            {
                hash.Append(piece).Append('\n');
            }

            return hash.ToString();
        }

        private static string Quantize(Vector3 value)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"({MathF.Round(value.X * 10000f)},{MathF.Round(value.Y * 10000f)},{MathF.Round(value.Z * 10000f)})");
        }
    }
}
