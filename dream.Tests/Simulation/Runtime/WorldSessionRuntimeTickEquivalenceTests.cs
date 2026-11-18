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
    // Checkpoints 6-10 were re-pinned in the wave-6 fix loop: a respawned wolf now takes a fresh
    // actor id instead of reusing 2000. Forcing the allocator back to 2000 reproduces the old values.
    // Checkpoints 6-10 were re-pinned again in NETR-3: the secondaries (who hold attack from tick
    // FightFrom) now go through the attack-interval repeat gate instead of resolving a use on every
    // received command. Resolving secondary uses from the drained frames again, with no gate,
    // reproduces the wave-6 values (0x5497DAFE6A7BEBBC, 0x1F4BB5B39F201942, 0x3385C15304627C69,
    // 0xDFC4BD7E7472FF36, 0xD63DD1835BEC99AB).
    // Checkpoints 2-10 were re-pinned when a chasing wolf began to stop inside its bite range (about 1.3 m
    // instead of 2.0 m): the wolf walks farther and bites a player that stands still. Checkpoint 1 is
    // unchanged. The previous values were 0xC73284FF379D57EA, 0x7E52DBC5EC2E6514, 0x48E4F56A857E8DC0,
    // 0xE74ADEE40AD629DA, 0x17AF03F609BF31E8, 0x6A21C08ACFFABED0, 0x5E2E975D48F4D58A,
    // 0xF80703360DEEA88F, 0x1DBCE7E3D7E4C392.
    // Checkpoints 6-10 were re-pinned in the last fix loop of wave I: the respawned wolf spawns in the far corner
    // ((12.5, 1, 12.5) on this 16 m map) instead of beside the spawn, so it no longer comes back to fight the
    // players. Checkpoints 1-5 are unchanged (the test now places the first wolf at (5.5, 1, 2.5) and ends the
    // join windows, which reproduces the old run exactly up to the wolf's death). The previous values were
    // 0x8118838F28D2A8DB, 0x896E506A472FAAF5, 0x7FB267DD0A1ED1E7, 0xA399D97471F2108A, 0x626AC8A4F7C77C1E.
    // Re-pinned once by M2-C (fair kills): the XP curve default is now 100 + level * 50 (was 1000 + level * 23),
    // which reaches the snapshot from checkpoint 1, and the wolf's kill shares XP among its contributors and
    // sends the loot to the top damage dealer. The previous values were 0x154E692D35B2DD4E, 0x308F1C0DD0CD35B4,
    // 0xB2C035E98C54CB57, 0x58DC81E03DE29867, 0x73E33D5492ADD38C, 0x47B5F355035AF99E, 0x1A65C2CE9810486A,
    // 0xB2A5C4A1D0C2DEE1, 0xA0CDA305A4CCCCD4, 0x55BC8DA8232AA9FB.
    // Re-pinned once by the stats sheet merge (protocol 19): every player's PlayerCombatStatsSnapshot now
    // carries Agility and Energy, so the record text of each actor in the envelope differs from checkpoint 1;
    // the run itself (hits, deaths, mutations, results) is unchanged. The previous values were
    // 0x57811F4CEEEA1202, 0x2254F43763F2C692, 0x49DC4557820EEEDB, 0x79937DA447E64F03, 0xAC348FD10F524A0D,
    // 0xB079B6B30288666D, 0x92535FAA16E90FE5, 0xB516BC543298CF5A, 0x13A524EB4D26ADD3, 0x3DCDD6A338F05900.
    // Re-pinned again: a fresh PlayerCombatStats now defaults Agility and Energy to 10 (StartAgility/StartEnergy)
    // instead of 0, so the actor record text carries 10,10; the run itself is unchanged. Previous values:
    // 0x6A91D3B7679F05EA, 0xD8D71AAABAD4E890, 0x15B1892906AE5247, 0xB059BEBAC4DB7E91, 0x42F4E81D5A5D3325,
    // 0x8A59A8D602033E65, 0xBDE658AA2C0D89EF, 0xA048351F730747CC, 0x46384E948B877B11, 0xCC7D87F23B421C08.
    // Re-pinned by the character-gaits wave (fix loop 1), and this one moved the run itself, not
    // just the record text. The movement contract changed: the player's single flat 6 m/s became
    // three tiers (walk 1.6, run cruise 4.3, sprint 6.0) and PlayerMotor ramps into them at
    // DefaultGroundAcceleration 18 m/s^2 / DefaultGroundDeceleration 24 m/s^2 instead of assigning
    // the commanded velocity outright, and the enemies were re-based with it (this script's wolf
    // patrols at 1.4 and chases at 5.0). So every walking tick of the script puts the seats
    // somewhere new: over the 20 walk ticks the ramp covers
    // 0.5 x 18 x (20 / 64)^2 = 0.88 m where the old motor covered 20 / 64 x 6 = 1.88 m, and the
    // positions hashed into checkpoint 1 differ from the first walking tick on. ALL ten checkpoints
    // moved, which is the expected shape of this change (a quietly broken golden would move a
    // trailing subset or leave the run's shape different).
    //
    // What the script still does, unchanged, is what makes the new values trustworthy: the build
    // is accepted, the wolf hits players 3 times, players hit enemies 8 times, 2 wolves die,
    // 2 mutations export, 20 queued same-tick actions are processed and the wolf chases for 87
    // ticks -- the asserts below, all of which still pass against the regenerated hashes.
    // The previous values were 0x47C9098CA55E6A2A, 0x75C725B66AC87974, 0xC5C7582BCA65AB27,
    // 0xFA8959A59E2744C9, 0xB1D85FAD2772C596, 0xD2EF1B9E1AE71866, 0xBF218B5A237681DA,
    // 0x589E0C5D08B17BC1, 0xA1F2C1A7DCC7D338, 0xC3A8C27C48C84BA2.
    // Re-pinned again by the player-speeds change: the default cruise, the sprint and the walk floor
    // were slowed, so every walking tick puts the seats somewhere new and all ten checkpoints moved.
    // The script's shape is unchanged: the build is accepted, the wolf hits players 3 times, players
    // hit enemies 8 times, 2 wolves die, 2 mutations export and 20 queued actions are processed
    // (the wolf now chases for 91 ticks). The previous values were
    // 0x1A1BAA6E45BBACD3, 0x040DC65A79CDD59F, 0xBC1CC1AC529E604A, 0xF1E4FE4528377000, 0xB709A19E46CCC057,
    // 0x015F2B430951B84B, 0xEAF32C18E2FF7370, 0x6013610680906590, 0x7EC02EE1E4B36B83, 0x18F2DDA4A60C2E40.
    private static readonly ulong[] GoldenCheckpoints =
    [
        0x3DC5F3B5EF51BCCEUL, 0x2A79F9C880D771C6UL, 0xEAA7CDE2A30B6FCCUL, 0x931A5174CA7B7091UL, 0x1E73BCE72892C7BCUL,
        0x24C6ADDAD2F642FEUL, 0x31EFA8E2A7F09351UL, 0x0DD188E4D622C4F1UL, 0x27E966A1972E9736UL, 0x65D2F5003D1A9FCFUL
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
        // The script was written for a day wolf beside the spawn and for players who can be bitten at once. The
        // wolf now spawns in the far corner and a join is protected for 192 ticks, so the test places the wolf
        // where it used to spawn and ends the join windows.
        host.Runtime.Enemies.ActiveActors[0].SetPosition(new Vector3(5.5f, 1f, 2.5f));
        foreach (string session in new[] { PrimarySession, SessionC, SessionA })
        {
            WaveIRuntimeKit.EndProtection(host.Runtime, new PlayerSessionId(session));
        }

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
