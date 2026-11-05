using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.Runtime;

// Wave 9 fix loop 2 (W9-2 follow-ups).
//
// S1: a snapshot taken after k starved ticks holds the server's position with the acknowledged
// command repeated k times. When the player changed input during the starve (idle at the ack, then
// walking), the client's sample k ticks after the ack has walked: comparing with it pulled the
// player back, and the server's replay of the walk then pushed it forward again. The reconciler
// now compares with what the server simulated unless the client's own commands match it.
//
// S2: after a starve, a backlog that arrives without its newest tick leaves skip debt in the inbox
// (the newest pending tick is never skipped), so the server is that many ticks ahead of its ack.
// The runtime now adds that carried debt to StarvedTicks.
//
// Both run the server through BufferedPlayerCommandInbox and RuntimeAuthoritativeSessionHost with
// a steady seat beside the stalled one, the stalled one as primary and as secondary seat, and an
// unstalled run of the same commands as the client's prediction.
public class Wave9StarveInputChangeTests(ITestOutputHelper output)
{
    private const string Steady = "w9s-steady";
    private const string Walker = "w9s-walker";
    // Odd, so at 32 Hz the ack's own tick sends no snapshot and the first snapshot with that ack
    // is a starved one (a snapshot of the ack's own tick would be compared first, and the starved
    // ones that repeat its ack ignored).
    private const int StartTick = 21;
    private const int StallTicks = 4;
    // Short of the inbox's shed window, whose latency cut is a real (and separate) correction.
    private const int RunTicks = StartTick + StallTicks + 24;

    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    private enum Arrival
    {
        // The stalled frames arrive together with the next tick's own frame.
        WithNewest,
        // The stalled frames arrive alone; from then on every frame is a tick late.
        WithoutNewest,
        // As WithoutNewest, until a later tick brings two frames and the delivery catches up.
        WithoutNewestThenCatchUp,
    }

    private const int CatchUpAfterTicks = 3;

    // LastCommandTick: the last tick that popped a frame for the walker.
    private readonly record struct ServerSample(ulong Tick, ulong Ack, int StarvedTicks, Vector3 Position, ulong LastCommandTick = 0);

    private readonly record struct Correction(ulong Tick, ulong Ack, Vector3 Error, ClientReconciliationKind Kind, int StarvedTicks = 0);

    // S1: idle at the ack, walking from the next tick, four ticks starved by the network.
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public void InputChangeDuringAStarve_IsNotCorrectedBackAndForth(bool walkerIsPrimary, int snapshotIntervalTicks)
    {
        Vector3 facing = FindOpenFacing(walkerIsPrimary);
        static float Forward(int tick) => ForwardOf(Input.IdleThenWalk, tick);
        IReadOnlyList<ServerSample> predicted = SharedRun(walkerIsPrimary, facing, Input.IdleThenWalk, stalled: false, Arrival.WithNewest);
        IReadOnlyList<ServerSample> server = SharedRun(walkerIsPrimary, facing, Input.IdleThenWalk, stalled: true, Arrival.WithNewest);

        Assert.Equal(StallTicks, server[StartTick + StallTicks - 1].StarvedTicks);

        List<Correction> fixedDivergences = [];
        List<Correction> legacyDivergences = [];
        List<Correction> fixedCorrections = Reconcile(predicted, server, Forward, snapshotIntervalTicks, recordMovement: true, out float fixedMax, fixedDivergences);
        List<Correction> legacyCorrections = Reconcile(predicted, server, Forward, snapshotIntervalTicks, recordMovement: false, out float legacyMax, legacyDivergences);
        float walked = Horizontal(predicted[^1].Position - predicted[0].Position);
        output.WriteLine(
            $"walker {(walkerIsPrimary ? "primary" : "secondary")} @{64 / snapshotIntervalTicks} Hz, walked {walked:F2} m: "
            + $"max error {fixedMax:F4} m with movement recorded ({fixedCorrections.Count} corrections), "
            + $"{legacyMax:F4} m without ({legacyCorrections.Count}: {Describe(legacyCorrections)}); "
            + $"starved-snapshot comparison fixed [{Describe(Starved(fixedDivergences))}] legacy [{Describe(Starved(legacyDivergences))}]");

        Assert.True(walked > 1f, $"only walked {walked} m");
        Assert.True(fixedMax <= ClientPredictionReconciler.DefaultDeadbandDistance, $"max error {fixedMax} m: {Describe(fixedCorrections)}");
        Assert.Empty(fixedCorrections);
        if (snapshotIntervalTicks == 2)
        {
            // Character-gaits wave: this used to assert a back-and-forth PAIR of corrections from
            // the legacy comparison. That premise died with the instant-velocity motor, and not
            // just its number:
            //   * _lastComparedSequence makes the first starved snapshot the only one compared
            //     under its ack, so the artefact is exactly one tick wide;
            //   * the client is idle at the ack and walking from the next tick, so with a ramp its
            //     one divergent tick covers a * dt^2 = 18 / 64^2 = 0.0044 m (measured 0.0034 -
            //     0.0037 m with floor friction), where the old motor jumped straight to 6 m/s and
            //     covered 6 / 64 = 0.094 m;
            //   * 0.0044 m is a ninth of DefaultDeadbandDistance (0.05 m), so the legacy
            //     comparison no longer *emits* a correction at all -- and with no correction there
            //     is nothing for the next ack to undo, which is the second half of the pair.
            // No input change can produce a deadband-sized one-tick error any more, because
            // velocity itself cannot change by more than GroundAcceleration * dt = 0.28 m/s in a
            // tick. So the premise is re-expressed on the comparison error the fix actually
            // changes, at the one starved snapshot that is compared: the legacy comparison is a
            // full acceleration tick off and points backwards along the walk (it would pull the
            // player back), the fixed one is on the server's own extrapolated step and is exact.
            Correction legacyStarved = Assert.Single(Starved(legacyDivergences));
            Assert.Equal(1, legacyStarved.StarvedTicks);
            Assert.True(legacyStarved.Error.Length > 0.002f, $"legacy starved comparison only off by {legacyStarved.Error.Length} m");
            Assert.True(
                Vector3.Dot(legacyStarved.Error, facing) < 0f,
                $"the legacy comparison should pull the walker back along {facing}: {legacyStarved.Error}");

            // The fixed comparison models what the server did in that tick: the ack is the idle
            // command, whose target speed is zero, so the motor's brake (character-gaits fix loop
            // 3) leaves the body where it was. What is left is the body's own residual drift for
            // that tick -- settling, or a slow slide on the spawn's slope, neither of which any
            // motor model can derive from tuning constants: measured 0.00023 m on the primary
            // seat's spawn and 0.00092 m on the secondary's. Either way a fiftieth of the
            // deadband, and far inside the legacy comparison's error. (The bound used to be a
            // tenth of the legacy error, which held only because the old extrapolation repeated
            // the ack tick's displacement verbatim and so happened to carry that drift along.)
            Assert.All(Starved(fixedDivergences), divergence =>
                Assert.True(
                    divergence.Error.Length < 0.002f
                        && divergence.Error.Length < legacyStarved.Error.Length / 2f,
                    $"fixed starved comparison off by {divergence.Error.Length} m"));
        }
    }

    private static List<Correction> Starved(List<Correction> divergences) =>
        divergences.Where(divergence => divergence.StarvedTicks > 0).ToList();

    // S2: walking, four ticks starved, the backlog arrives without its newest tick: the inbox
    // skips three and keeps one tick of debt, so the server is a tick ahead of its ack.
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public void CarriedSkipDebt_IsCountedInStarvedTicks_AndNotCorrected(bool walkerIsPrimary, int snapshotIntervalTicks)
    {
        Vector3 facing = FindOpenFacing(walkerIsPrimary);
        static float Forward(int tick) => ForwardOf(Input.Walk, tick);
        IReadOnlyList<ServerSample> predicted = SharedRun(walkerIsPrimary, facing, Input.Walk, stalled: false, Arrival.WithNewest);
        IReadOnlyList<ServerSample> server = SharedRun(walkerIsPrimary, facing, Input.Walk, stalled: true, Arrival.WithoutNewest);

        int resumeIndex = StartTick + StallTicks;
        ServerSample resumed = server[resumeIndex];
        Assert.Equal((ulong)(StartTick + StallTicks), resumed.Ack);
        Assert.Equal(1, resumed.StarvedTicks);
        int debtTicks = 0;
        for (int i = resumeIndex; i < server.Count && server[i].StarvedTicks > 0; i++)
        {
            // The position is exactly the prediction StarvedTicks ticks after the ack.
            ServerSample sample = server[i];
            Assert.Equal(predicted[(int)sample.Ack + sample.StarvedTicks - 1].Position, sample.Position);
            debtTicks++;
        }

        List<Correction> corrections = Reconcile(predicted, server, Forward, snapshotIntervalTicks, recordMovement: true, out float max);
        output.WriteLine(
            $"walker {(walkerIsPrimary ? "primary" : "secondary")} @{64 / snapshotIntervalTicks} Hz: debt reported for {debtTicks} ticks, "
            + $"max error {max:F4} m, corrections: {Describe(corrections)}");

        Assert.True(debtTicks >= 4, $"debt reported for {debtTicks} ticks");
        // While the debt is held nothing is corrected. Once the inbox drops it (a ring's worth of
        // pops without a starve), the server's extra tick is real and corrected once.
        Assert.DoesNotContain(corrections, correction => correction.Tick <= server[resumeIndex + debtTicks - 1].Tick);
        Assert.False(HasBackAndForthPair(corrections), Describe(corrections));
        Assert.True(corrections.Count <= 1, Describe(corrections));
    }

    // S2 where the debt is spent: the delivery catches up (two frames on one tick), the inbox
    // skips the carried tick, and the ack meets the server again. Counting the debt keeps every
    // snapshot on the prediction; leaving it out corrected the extra tick and then undid it.
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public void CarriedSkipDebt_SpentByACatchUp_IsNeverCorrected(bool walkerIsPrimary, int snapshotIntervalTicks)
    {
        Vector3 facing = FindOpenFacing(walkerIsPrimary);
        static float Forward(int tick) => ForwardOf(Input.Walk, tick);
        IReadOnlyList<ServerSample> predicted = SharedRun(walkerIsPrimary, facing, Input.Walk, stalled: false, Arrival.WithNewest);
        IReadOnlyList<ServerSample> server = SharedRun(walkerIsPrimary, facing, Input.Walk, stalled: true, Arrival.WithoutNewestThenCatchUp);

        Assert.Equal(predicted[^1].Position, server[^1].Position);
        Assert.Equal(predicted[^1].Ack, server[^1].Ack);
        List<Correction> corrections = Reconcile(predicted, server, Forward, snapshotIntervalTicks, recordMovement: true, out float max);
        List<Correction> uncounted = Reconcile(predicted, WithoutCarriedDebt(server), Forward, snapshotIntervalTicks, recordMovement: true, out float uncountedMax);
        output.WriteLine(
            $"walker {(walkerIsPrimary ? "primary" : "secondary")} @{64 / snapshotIntervalTicks} Hz, debt spent by a catch-up: "
            + $"max error {max:F4} m ({Describe(corrections)}); without the debt counted {uncountedMax:F4} m ({Describe(uncounted)})");

        Assert.True(max <= ClientPredictionReconciler.DefaultDeadbandDistance, Describe(corrections));
        Assert.Empty(corrections);
        Assert.True(HasBackAndForthPair(uncounted), Describe(uncounted));
    }

    // StarvedTicks as the runtime reported it before the carried debt was counted: only the
    // ticks since the session's last command (the ack's own tick and its starved run).
    private static List<ServerSample> WithoutCarriedDebt(IReadOnlyList<ServerSample> server)
    {
        List<ServerSample> samples = [];
        foreach (ServerSample sample in server)
        {
            samples.Add(sample with { StarvedTicks = Math.Min(sample.StarvedTicks, (int)(sample.Tick - sample.LastCommandTick)) });
        }

        return samples;
    }

    // Accessor semantics: the debt of the starved run in progress is the runtime's own count and
    // is left out; what a partial backlog leaves behind is carried.
    [Fact]
    public void GetCarriedRepeatDebtTicks_LeavesOutTheRunInProgress()
    {
        BufferedPlayerCommandInbox inbox = new();
        PlayerSessionId id = new(Walker);
        List<PlayerSessionId> sessions = [id];
        List<PlayerCommandFrame> drained = [];
        for (ulong tick = 1; tick <= 3; tick++)
        {
            inbox.Enqueue(Command(Walker, tick, 1f, Vector3.UnitX));
            inbox.DrainInto(sessions, drained);
        }

        for (int starved = 1; starved <= 4; starved++)
        {
            inbox.DrainInto(sessions, drained);
            Assert.Empty(drained);
            Assert.Equal(starved, inbox.GetSkipDebtTicks(id));
            Assert.Equal(0, inbox.GetCarriedRepeatDebtTicks(id));
        }

        for (ulong held = 4; held <= 7; held++)
        {
            inbox.Enqueue(Command(Walker, held, 1f, Vector3.UnitX));
        }

        inbox.DrainInto(sessions, drained);
        Assert.Equal(7UL, Assert.Single(drained).Sequence);
        Assert.Equal(1, inbox.GetCarriedRepeatDebtTicks(id));
        inbox.DrainInto(sessions, drained);
        Assert.Empty(drained);
        Assert.Equal(2, inbox.GetSkipDebtTicks(id));
        Assert.Equal(1, inbox.GetCarriedRepeatDebtTicks(id));
        Assert.Equal(0, inbox.GetCarriedRepeatDebtTicks(new PlayerSessionId("unknown")));
    }

    // Debt no skip has touched is not carried: a client that dropped its stalled ticks (a frame
    // stall) resumes one frame per tick, never sends the backlog that would spend it, and the
    // server's extra ticks are a real divergence, corrected as they show rather than all at once
    // when the debt expires.
    [Fact]
    public void GetCarriedRepeatDebtTicks_IgnoresDebtNoSkipHasSpent()
    {
        BufferedPlayerCommandInbox inbox = new();
        PlayerSessionId id = new(Walker);
        List<PlayerSessionId> sessions = [id];
        List<PlayerCommandFrame> drained = [];
        inbox.Enqueue(Command(Walker, 1, 1f, Vector3.UnitX));
        inbox.DrainInto(sessions, drained);
        for (int starved = 1; starved <= 4; starved++)
        {
            inbox.DrainInto(sessions, drained);
        }

        for (ulong sequence = 2; sequence <= 4; sequence++)
        {
            inbox.Enqueue(Command(Walker, sequence, 1f, Vector3.UnitX));
            inbox.DrainInto(sessions, drained);
            Assert.Equal(sequence, Assert.Single(drained).Sequence);
            Assert.Equal(4, inbox.GetSkipDebtTicks(id));
            Assert.Equal(0, inbox.GetCarriedRepeatDebtTicks(id));
        }
    }

    // After a stall longer than the repeat window, only the repeated ticks are carried: the idle
    // ones did not move the player, and counting them would hide where the repeat run ended.
    [Fact]
    public void GetCarriedRepeatDebtTicks_LeavesOutIdleDebt()
    {
        BufferedPlayerCommandInbox inbox = new();
        PlayerSessionId id = new(Walker);
        List<PlayerSessionId> sessions = [id];
        List<PlayerCommandFrame> drained = [];
        inbox.Enqueue(Command(Walker, 1, 1f, Vector3.UnitX));
        inbox.DrainInto(sessions, drained);
        int starvedTicks = WorldSessionRuntime.MaxCommandRepeatTicks + 4;
        for (int starved = 1; starved <= starvedTicks; starved++)
        {
            inbox.DrainInto(sessions, drained);
        }

        for (ulong held = 2; held <= 5; held++)
        {
            inbox.Enqueue(Command(Walker, held, 1f, Vector3.UnitX));
        }

        inbox.DrainInto(sessions, drained);
        Assert.Equal(5UL, Assert.Single(drained).Sequence);
        Assert.Equal(starvedTicks - 3, inbox.GetSkipDebtTicks(id));
        Assert.Equal(WorldSessionRuntime.MaxCommandRepeatTicks - 3, inbox.GetCarriedRepeatDebtTicks(id));
    }

    // The reconciler alone: a starved snapshot compares with the client's sample only when the
    // client's commands for those ticks are the ack's movement (then idle) without a press.
    [Theory]
    [InlineData("walk-walk", false)]
    [InlineData("walk-idle", false)]
    [InlineData("idle-walk", true)]
    [InlineData("walk-jump", true)]
    [InlineData("walk-strafe", true)]
    public void StarvedSnapshot_UsesTheClientSampleOnlyWhenItsCommandsMatch(string scenario, bool extrapolates)
    {
        // The run cruise, so the repeated command's tier speed is the one the step implies: the
        // extrapolation ramps the step's speed toward the ack command's tier (character-gaits fix
        // loop 3), and a hand-picked 0.07 m/tick stands for 4.48 m/s, which the server would brake
        // back to the 4.3 m/s cruise. The scenario is about which sample is compared, not about
        // the tier, so the step is simply the cruise one.
        const float Step = PlayerSimulationTuning.DefaultRunSpeed / SimulationClock.AuthoritativeTicksPerSecond;
        PlayerMoveIntent walk = new(1f, 0f);
        (PlayerMoveIntent atAck, PlayerMoveIntent after) = scenario switch
        {
            "walk-walk" => (walk, walk),
            "walk-idle" => (walk, PlayerMoveIntent.None),
            "idle-walk" => (PlayerMoveIntent.None, walk),
            "walk-jump" => (walk, walk with { JumpPressed = true }),
            _ => (walk, new PlayerMoveIntent(0f, 1f)),
        };
        ClientPredictionReconciler reconciler = new();
        Vector3 position = Vector3.Zero;
        for (ulong tick = 1; tick <= 10; tick++)
        {
            PlayerMoveIntent move = tick <= 6 ? atAck : after;
            position += move == PlayerMoveIntent.None ? Vector3.Zero : new Vector3(Step, 0f, 0f);
            reconciler.RecordPrediction(tick, position, move);
        }

        // Ack 6 with 4 starved ticks: the server repeated the command of tick 6.
        Vector3 atSix = atAck == PlayerMoveIntent.None ? Vector3.Zero : new Vector3(6 * Step, 0f, 0f);
        Vector3 serverRepeated = atSix + (atAck == PlayerMoveIntent.None ? Vector3.Zero : new Vector3(4 * Step, 0f, 0f));
        Vector3 clientSample = position;
        Vector3 server = extrapolates ? serverRepeated : clientSample;
        ClientReconciliationResult result = reconciler.Reconcile(6, server, starvedTicks: 4);

        Assert.Equal(ClientReconciliationKind.None, result.Kind);
        Assert.True(result.Error.Length < 1e-4f, $"{scenario}: error {result.Error}");
    }

    // Extrapolating unpredicted starved ticks while falling: gravity changes every repeated tick's
    // vertical step, which the linear repeat missed by g dt^2 k(k+1)/2 (0.086 m after 8 ticks).
    [Fact]
    public void FallingPrediction_ExtrapolatedWithGravity_IsNotCorrectedAfterEightStarvedTicks()
    {
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Walker, Walker);
        runtime.Enemies.Clear();
        runtime.Player.SetWorldPosition(new Vector3(5f, 30f, 5f));
        ClientPredictionReconciler reconciler = new();
        PlayerMoveIntent walk = new(1f, 0f);
        for (ulong tick = 1; tick <= 16; tick++)
        {
            host.AdvanceTick(tick, SimulationClock.FixedDeltaSeconds, [Command(Walker, tick, 1f, Vector3.UnitX)], Tuning, allowGameplaySimulation: false);
            reconciler.RecordPrediction(tick, WalkerPosition(runtime), walk);
        }

        Vector3 atAck = WalkerPosition(runtime);
        for (ulong tick = 17; tick <= 24; tick++)
        {
            host.AdvanceTick(tick, SimulationClock.FixedDeltaSeconds, [], Tuning, allowGameplaySimulation: false);
        }

        ActorSnapshot actor = Assert.Single(runtime.CaptureActorSnapshots(), candidate => candidate.SessionId == Walker);
        Assert.Equal(8, actor.StarvedTicks);
        ClientReconciliationResult result = reconciler.Reconcile(16, actor.Position, starvedTicks: actor.StarvedTicks);
        output.WriteLine($"falling, 8 starved ticks extrapolated: moved {(actor.Position - atAck).Length:F3} m, error {result.Error.Length:F4} m ({result.Kind})");
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
        Assert.True(result.Error.Length < 0.02f, $"error {result.Error.Length} m");
    }

    // divergences, when given, collects every comparison with a non-zero error, not just the ones
    // above the deadband that become corrections: with the character-gaits ramp a one-tick
    // comparison artefact is smaller than the deadband but still measurable (see
    // InputChangeDuringAStarve_IsNotCorrectedBackAndForth).
    private static List<Correction> Reconcile(
        IReadOnlyList<ServerSample> predicted,
        IReadOnlyList<ServerSample> server,
        Func<int, float> forward,
        int snapshotIntervalTicks,
        bool recordMovement,
        out float maxError,
        List<Correction>? divergences = null)
    {
        ClientPredictionReconciler reconciler = new();
        List<Correction> corrections = [];
        maxError = 0f;
        for (int i = 0; i < server.Count; i++)
        {
            // The client predicts each tick as it sends it: by the server's tick T it has predicted T.
            ServerSample sample = predicted[i];
            PlayerMoveIntent move = new(forward((int)sample.Tick), 0f);
            if (recordMovement)
            {
                reconciler.RecordPrediction(sample.Tick, sample.Position, move);
            }
            else
            {
                reconciler.RecordPrediction(sample.Tick, sample.Position);
            }

            ServerSample snapshot = server[i];
            if (snapshot.Tick % (ulong)snapshotIntervalTicks != 0 || snapshot.Ack == 0)
            {
                continue;
            }

            ClientReconciliationResult result = reconciler.Reconcile(snapshot.Ack, snapshot.Position, starvedTicks: snapshot.StarvedTicks);
            maxError = Math.Max(maxError, result.Error.Length);
            if (result.Kind != ClientReconciliationKind.None)
            {
                corrections.Add(new Correction(snapshot.Tick, snapshot.Ack, result.Error, result.Kind, snapshot.StarvedTicks));
            }

            if (result.Error.Length > 1e-5f)
            {
                divergences?.Add(new Correction(snapshot.Tick, snapshot.Ack, result.Error, result.Kind, snapshot.StarvedTicks));
            }
        }

        return corrections;
    }

    // Two corrections in opposite directions: the second undoes the first.
    private static bool HasBackAndForthPair(List<Correction> corrections)
    {
        for (int i = 1; i < corrections.Count; i++)
        {
            if (Vector3.Dot(corrections[i - 1].Error, corrections[i].Error) < 0f)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(List<Correction> corrections) =>
        corrections.Count == 0
            ? "none"
            : string.Join(", ", corrections.Select(c => $"tick {c.Tick} ack {c.Ack} {c.Kind} {c.Error.Length:F3} m ({c.Error.X:F3}, {c.Error.Y:F3}, {c.Error.Z:F3})"));

    private static float Horizontal(Vector3 delta) => new Vector2(delta.X, delta.Z).Length;

    // The probe's answer depends only on the seat, so it runs once per seat for the class
    // (ARCH-1c: it built up to four runtimes in every one of the twelve theory cases).
    private static readonly ProbeCache<bool, Vector3> OpenFacings = new(ProbeOpenFacing);

    private static Vector3 FindOpenFacing(bool walkerIsPrimary) => OpenFacings.Get(walkerIsPrimary);

    private static Vector3 ProbeOpenFacing(bool walkerIsPrimary)
    {
        foreach (Vector3 facing in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
        {
            IReadOnlyList<ServerSample> probe = SharedRun(walkerIsPrimary, facing, Input.Walk, stalled: false, Arrival.WithNewest);
            Vector3 moved = probe[^1].Position - probe[0].Position;
            if (Horizontal(moved) > 2.5f && MathF.Abs(moved.Y) < 0.1f)
            {
                return facing;
            }
        }

        throw new InvalidOperationException("No open walk from the spawn.");
    }

    // The walker's input: idle at the ack and walking from the tick after it (S1), or walking
    // throughout (S2, and the facing probe).
    private enum Input
    {
        IdleThenWalk,
        Walk,
    }

    private static float ForwardOf(Input input, int tick) => input == Input.Walk || tick > StartTick ? 1f : 0f;

    // Run is deterministic in its inputs, and the theory cases differ in what they assert, not in
    // what they simulate: both snapshot rates of a scenario reconcile the same two runs, S2 and S3
    // share their prediction, and that prediction is the facing probe's walk. So each distinct run
    // is simulated once for the class (ARCH-1c: 10 runtimes instead of up to 32). The lists are
    // shared between tests and read only.
    private static readonly ProbeCache<(bool WalkerIsPrimary, Vector3 Facing, Input Input, bool Stalled, Arrival Arrival), IReadOnlyList<ServerSample>> Runs =
        new(key => Run(key.WalkerIsPrimary, key.Facing, tick => ForwardOf(key.Input, tick), key.Stalled, key.Arrival).AsReadOnly());

    private static IReadOnlyList<ServerSample> SharedRun(bool walkerIsPrimary, Vector3 facing, Input input, bool stalled, Arrival arrival) =>
        Runs.Get((walkerIsPrimary, facing, input, stalled, arrival));

    // The walker produces frame N on tick N. Unstalled, it arrives that tick. Stalled, frames
    // StartTick+1 .. StartTick+StallTicks are held and arrive on the tick after the last of them,
    // with that tick's own frame or (WithoutNewest) alone, every later frame then a tick late.
    // The runtime reads the inbox's carried repeat debt, as DedicatedServerHost wires it.
    private static List<ServerSample> Run(
        bool walkerIsPrimary,
        Vector3 facing,
        Func<int, float> forward,
        bool stalled,
        Arrival arrival)
    {
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        if (walkerIsPrimary)
        {
            host.JoinSession(Walker, Walker);
            host.JoinSession(Steady, Steady);
        }
        else
        {
            host.JoinSession(Steady, Steady);
            host.JoinSession(Walker, Walker);
        }

        runtime.Enemies.Clear();
        BufferedPlayerCommandInbox inbox = new();
        runtime.CarriedRepeatDebtTicks = inbox.GetCarriedRepeatDebtTicks;
        List<PlayerSessionId> sessions = [new PlayerSessionId(Steady), new PlayerSessionId(Walker)];
        List<PlayerCommandFrame> commands = [];
        List<ServerSample> samples = [];
        PlayerSessionId walker = new(Walker);
        ulong ack = 0;
        int stallStart = StartTick + 1;
        int stallEnd = StartTick + StallTicks;
        int lastSent = 0;
        ulong lastCommandTick = 0;
        for (int tick = 1; tick <= RunTicks; tick++)
        {
            inbox.Enqueue(Command(Steady, (ulong)tick, 0f, facing));
            if (!stalled || tick < stallStart)
            {
                inbox.Enqueue(Command(Walker, (ulong)tick, forward(tick), facing));
            }
            else if (tick > stallEnd)
            {
                if (tick == stallEnd + 1)
                {
                    for (int held = stallStart; held <= stallEnd; held++)
                    {
                        inbox.Enqueue(Command(Walker, (ulong)held, forward(held), facing));
                    }
                }

                bool late = arrival == Arrival.WithoutNewest
                    || (arrival == Arrival.WithoutNewestThenCatchUp && tick < stallEnd + 1 + CatchUpAfterTicks);
                int newest = late ? tick - 1 : tick;
                for (int frame = Math.Max(lastSent + 1, stallEnd + 1); frame <= newest; frame++)
                {
                    inbox.Enqueue(Command(Walker, (ulong)frame, forward(frame), facing));
                    lastSent = frame;
                }
            }

            inbox.DrainInto(sessions, commands);
            foreach (PlayerCommandFrame command in commands)
            {
                if (command.SessionId == walker)
                {
                    ack = command.Sequence;
                    lastCommandTick = (ulong)tick;
                }
            }

            host.AdvanceTick((ulong)tick, SimulationClock.FixedDeltaSeconds, commands, Tuning, allowGameplaySimulation: false);
            ActorSnapshot actor = Assert.Single(runtime.CaptureActorSnapshots(), candidate => candidate.SessionId == Walker);
            samples.Add(new ServerSample((ulong)tick, ack, actor.StarvedTicks, actor.Position, lastCommandTick));
        }

        return samples;
    }

    private static Vector3 WalkerPosition(WorldSessionRuntime runtime) =>
        Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == Walker).Position;

    private static PlayerCommandFrame Command(string session, ulong sequence, float forward, Vector3 facing)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(session),
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(forward, 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }
}
