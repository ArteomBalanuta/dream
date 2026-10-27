using System.Globalization;
using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// MEM-3 equivalence: a scripted 200-tick run through RuntimeAuthoritativeSessionHost with three
// sessions (joined out of ordinal order) that walk, build a cube, fight the runtime's wolf while
// it chases them, send same-tick queued actions that must be sorted, and break the cube. Each
// tick's action results and exported snapshot (actor order and state, damage events, world
// mutations, accepted sequences) are hashed. The goldens were captured on the code before MEM-3,
// so the allocation work must reproduce the same actor order, target choice and outcomes.
public class WorldSessionRuntimeTickEquivalenceTests(ITestOutputHelper output)
{
    private const int Ticks = 200;
    private const int FloorSize = 16;
    private const int CheckpointInterval = 20;
    private const string PrimarySession = "session-b";
    private const string SessionA = "session-a";
    private const string SessionC = "session-c";

    private const int WalkUntil = 20;
    private const int BuildAt = 3;
    // The primary seat fights first; the secondaries join later, so the wolf lives long enough
    // to reach session-c and bite.
    private const int PrimaryFightFrom = 70;
    private const int FightFrom = 100;
    private const int CubeAttackFrom = 120;
    // session-c walks into the wolf's reach in this window so the wolf lands hits.
    private const int ApproachFrom = 30;
    private const int ApproachUntil = 100;

    private static readonly Vector3 CubeCell = new(7f, 1f, 5f);

    // Cumulative FNV-1a hash after every CheckpointInterval ticks, captured on the code before
    // MEM-3 (integrate/engine-iteration at 5b05629). Positions are quantized to 1e-4.
    private static readonly ulong[] GoldenCheckpoints =
    [
        0x154E692D35B2DD4EUL, 0xC73284FF379D57EAUL, 0x7E52DBC5EC2E6514UL, 0x48E4F56A857E8DC0UL, 0xE74ADEE40AD629DAUL,
        0xA67114A570E1A99AUL, 0x31E29046D8B738B6UL, 0x2E8FBCFE5A9A6D17UL, 0x62190D974D415BDBUL, 0x27A60E4DAEF09CD7UL
    ];

    [Fact]
    public void ScriptedRun_ProducesTheSameSnapshotsResultsAndTargetOrderAsBeforeMem3()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            RunScript(out ulong[] checkpoints, out RunOutcome outcome);

            output.WriteLine("checkpoints: " + string.Join(", ", checkpoints.Select(value => $"0x{value:X16}UL")));
            output.WriteLine(
                $"build accepted {outcome.BuildAccepted}, wolf hits on players {outcome.PlayerDamageEvents}, " +
                $"player hits on enemies {outcome.EnemyDamageEvents}, wolf deaths {outcome.EnemyDeaths}, " +
                $"mutations {outcome.Mutations}, queued results {outcome.QueuedResults}, chasing ticks {outcome.ChasingTicks}");

            Assert.True(outcome.BuildAccepted, "The scripted build was rejected.");
            Assert.True(outcome.PlayerDamageEvents > 0, "The wolf never hit a player.");
            Assert.True(outcome.EnemyDamageEvents > 0, "No player hit an enemy.");
            Assert.True(outcome.Mutations > 0, "No world mutation was exported.");
            Assert.True(outcome.QueuedResults > 0, "No queued action was processed.");
            Assert.True(outcome.ChasingTicks > 0, "The wolf never chased.");
            Assert.Equal(GoldenCheckpoints, checkpoints);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    // Players are listed primary seat first, then secondary seats in ordinal session-id order,
    // then enemies. This order decides nearest-target ties and the snapshot actor order.
    [Fact]
    public void SnapshotActorOrder_IsPrimaryThenSecondariesByOrdinalSessionIdThenEnemies()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        host.AdvanceTick(1, SimulationClock.FixedDeltaSeconds, [], PlayerSimulationTuning.AuthoritativeDefault);

        string[] order = host.Runtime.ExportServerSnapshotEnvelope().Actors
            .Select(actor => actor.Kind == CreatureKind.Player ? actor.SessionId! : $"enemy#{actor.ActorId}")
            .ToArray();

        Assert.Equal([PrimarySession, SessionA, SessionC, "enemy#2000"], order);
    }

    private static RuntimeAuthoritativeSessionHost CreateHost()
    {
        WorldSessionRuntime runtime = new(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        RuntimeAuthoritativeSessionHost host = new(runtime);
        // Joined out of ordinal order: session-b takes the primary seat.
        host.JoinSession(PrimarySession, "Player B");
        host.JoinSession(SessionC, "Player C");
        host.JoinSession(SessionA, "Player A");
        Assert.Equal(PrimarySession, host.Runtime.PrimaryRemoteSessionId?.Value);
        return host;
    }

    private static void RunScript(out ulong[] checkpoints, out RunOutcome outcome)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        WorldSessionRuntime runtime = host.Runtime;
        List<ulong> checkpointList = [];
        ulong cumulative = 14695981039346656037UL;
        ulong receiveOrder = 0;
        bool buildAccepted = false;
        int playerDamageEvents = 0;
        int enemyDamageEvents = 0;
        int enemyDeaths = 0;
        int mutations = 0;
        int queuedResults = 0;
        int chasingTicks = 0;
        StringBuilder text = new();

        for (int tick = 1; tick <= Ticks; tick++)
        {
            IReadOnlyList<ActorSnapshot> before = runtime.CaptureActorSnapshots();
            List<PlayerCommandFrame> commands = [];
            List<QueuedPlayerAction> queued = [];
            bool queuedTick = tick >= FightFrom && tick % 10 == 5;
            foreach (string session in new[] { SessionC, PrimarySession, SessionA })
            {
                bool isPrimary = session == PrimarySession;
                bool fighting = tick >= (isPrimary ? PrimaryFightFrom : FightFrom);
                bool cubeAttack = isPrimary && tick >= CubeAttackFrom;
                bool walking = tick <= WalkUntil;
                bool approaching = session == SessionC && tick >= ApproachFrom && tick <= ApproachUntil;
                Vector3 eye = EyeOf(before, session);
                Vector3 facing = Vector3.UnitZ;
                bool attack = false;
                if (cubeAttack)
                {
                    facing = DirectionFrom(eye, CubeCell + new Vector3(0.5f));
                    attack = true;
                }
                else if ((fighting || approaching) && TryGetNearestEnemyAnchor(runtime, eye, out Vector3 wolf))
                {
                    facing = DirectionFrom(eye, wolf);
                    attack = fighting && !(queuedTick && !isPrimary);
                }

                commands.Add(new PlayerCommandFrame(
                    new PlayerSessionId(session),
                    (ulong)tick,
                    new PlayerModeSwitchIntent(fighting || cubeAttack ? PlayerInteractionMode.Combat : PlayerInteractionMode.Building),
                    new PlayerMoveIntent((walking && !isPrimary) || approaching ? 1f : 0f, walking && isPrimary ? 0.5f : 0f),
                    new PlayerLookIntent(0f, 0f, facing),
                    PrimaryUsePressed: attack));

                if (queuedTick && !isPrimary)
                {
                    // Same receive order for both on odd decades, so the session-id tiebreak decides.
                    ulong order = tick % 20 == 5 ? receiveOrder : receiveOrder + (session == SessionC ? 0UL : 1UL);
                    queued.Add(new QueuedPlayerAction(
                        order,
                        new PlayerSessionId(session),
                        new PlayerActionMessage(
                            new PlayerSessionId(session),
                            (ulong)tick,
                            new CombatUseIntent(eye, facing))));
                }
            }

            if (tick == BuildAt)
            {
                queued.Add(new QueuedPlayerAction(
                    receiveOrder,
                    new PlayerSessionId(PrimarySession),
                    new PlayerActionMessage(new PlayerSessionId(PrimarySession), BuildAt, new BuildUseIntent(CubeCell))));
            }

            receiveOrder += 2;
            IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                commands,
                queued,
                PlayerSimulationTuning.AuthoritativeDefault);
            ServerSnapshotEnvelope envelope = host.ExportSnapshot();

            text.Clear();
            text.Append("tick ").Append(tick).Append('\n');
            foreach (AuthoritativeActionResult result in results)
            {
                text.Append("result ").Append(result.SessionId).Append(' ').Append(result.ActionSequence).Append(' ')
                    .Append(result.ServerTick).Append(' ').Append(result.Accepted).Append(' ')
                    .Append(result.Payload?.ToString() ?? "-").Append(' ')
                    .Append(result.Rejection?.Reason ?? "-").Append('\n');
                if (result.SessionId == PrimarySession && result.ActionSequence == BuildAt && result.Accepted)
                {
                    buildAccepted = true;
                }

                if (result.ActionSequence == (ulong)tick && queuedTick && result.SessionId != PrimarySession)
                {
                    queuedResults++;
                }
            }

            AppendEnvelope(text, envelope);
            ulong tickHash = Fnv(text.ToString());
            cumulative = (cumulative ^ tickHash) * 1099511628211UL;
            if (tick % CheckpointInterval == 0)
            {
                checkpointList.Add(cumulative);
            }

            foreach (DamageEventDelta damage in envelope.DamageEvents)
            {
                if (damage.Kind == CreatureKind.Player)
                {
                    playerDamageEvents++;
                }
                else if (damage.Kind == CreatureKind.Enemy)
                {
                    enemyDamageEvents++;
                    enemyDeaths += damage.ActorDied && damage.Amount > 0 ? 1 : 0;
                }
            }

            mutations += envelope.WorldMutations.Count;
            chasingTicks += envelope.Actors.Count(actor => actor.EnemyBehaviorState == EnemyBehaviorState.Chasing);
        }

        checkpoints = checkpointList.ToArray();
        outcome = new RunOutcome(buildAccepted, playerDamageEvents, enemyDamageEvents, enemyDeaths, mutations, queuedResults, chasingTicks);
    }

    private static void AppendEnvelope(StringBuilder text, ServerSnapshotEnvelope envelope)
    {
        text.Append("snapshot ").Append(envelope.ServerTick).Append(' ').Append(envelope.InteractionMode)
            .Append(' ').Append(envelope.WorldChecksum?.ToString("X16") ?? "-").Append(' ').Append(envelope.WorldChecksumSequence).Append('\n');
        foreach (KeyValuePair<string, ulong> sequence in envelope.LastAcceptedCommandSequences.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append("seq ").Append(sequence.Key).Append('=').Append(sequence.Value).Append('\n');
        }

        foreach (ActorSnapshot actor in envelope.Actors)
        {
            text.Append("actor ").Append(actor.ActorId).Append(' ').Append(actor.Kind).Append(' ')
                .Append(actor.SessionId ?? "-").Append(' ').Append(Format(actor.Position)).Append(' ')
                .Append(Format(actor.Facing)).Append(' ').Append(actor.CurrentHealth).Append('/').Append(actor.MaxHealth)
                .Append(' ').Append(actor.IsAlive).Append(' ').Append(actor.InteractionMode?.ToString() ?? "-")
                .Append(' ').Append(actor.CombatStats?.ToString() ?? "-").Append(' ').Append(actor.DisplayName ?? "-")
                .Append(' ').Append(actor.Progression?.ToString() ?? "-").Append(' ')
                .Append(actor.EnemyBehaviorState?.ToString() ?? "-").Append('\n');
        }

        foreach (WorldMutationDelta mutation in envelope.WorldMutations)
        {
            text.Append("mutation ").Append(mutation.Sequence).Append(' ').Append(mutation.Kind).Append(' ')
                .Append(Format(mutation.CellPosition)).Append(' ')
                .Append(mutation.TargetCellPosition.HasValue ? Format(mutation.TargetCellPosition.Value) : "-").Append(' ')
                .Append(mutation.CenterPosition.HasValue ? Format(mutation.CenterPosition.Value) : "-").Append(' ')
                .Append(mutation.MaterialId?.ToString() ?? "-").Append(' ').Append(mutation.ActorId?.ToString() ?? "-");
            if (mutation.FracturePieces != null)
            {
                foreach (var piece in mutation.FracturePieces)
                {
                    text.Append(' ').Append(Format(piece.Center)).Append(':').Append(piece.SideLength.ToString("F4"));
                }
            }

            text.Append('\n');
        }

        foreach (DamageEventDelta damage in envelope.DamageEvents)
        {
            text.Append("damage ").Append(damage).Append('\n');
        }
    }

    private static Vector3 EyeOf(IReadOnlyList<ActorSnapshot> actors, string session)
    {
        foreach (ActorSnapshot actor in actors)
        {
            if (actor.SessionId == session)
            {
                return actor.Position + new Vector3(0f, 0.35f, 0f);
            }
        }

        throw new InvalidOperationException($"No actor for {session}.");
    }

    private static bool TryGetNearestEnemyAnchor(WorldSessionRuntime runtime, Vector3 eye, out Vector3 anchor)
    {
        EnemyActor? nearest = runtime.Enemies.ActiveActors
            .Where(enemy => enemy.Combatant.Health.IsAlive)
            .OrderBy(enemy => (enemy.Position - eye).LengthSquared)
            .FirstOrDefault();
        anchor = nearest?.Combatant.GetCombatAnchorWorldPosition() ?? default;
        return nearest != null;
    }

    private static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? Vector3.UnitZ : direction.Normalized();
    }

    private static string Format(Vector3 value)
    {
        return $"({value.X:F4},{value.Y:F4},{value.Z:F4})";
    }

    private static ulong Fnv(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char character in text)
        {
            hash = (hash ^ character) * 1099511628211UL;
        }

        return hash;
    }

    private sealed record RunOutcome(
        bool BuildAccepted,
        int PlayerDamageEvents,
        int EnemyDamageEvents,
        int EnemyDeaths,
        int Mutations,
        int QueuedResults,
        int ChasingTicks);
}
