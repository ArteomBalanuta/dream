using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Runtime;

// NETR-5: the walker sends one command per tick (the NETR-4 client). Its commands stall in the
// network for StallTicks ticks and then arrive as one burst. Wave7StarvationReconciliationTests
// measured the latest-only inbox: the server idles the seat after 16 repeated ticks and moves it
// one tick on resume, so the client snaps back by the whole stall. The buffered inbox replays the
// burst one frame per tick, skipping the 16 ticks the runtime already repeated, which leaves only
// the ramp deficit of the character-gaits motor (ReplayRampDeficit) instead of a snap.
public class BufferedInboxStallReplayTests(ITestOutputHelper output)
{
    private const string Steady = "session-a";
    private const string Walker = "session-b";
    private const int WalkTicks = 8;

    // The stall is long enough that the latest-only inbox loses most of it (the seat idles after
    // MaxCommandRepeatTicks) and short enough for the buffered inbox's ring to replay it without its
    // elastic shed folding the backlog. At the default cruise the latest-only gap stays under the
    // reconciler's snap DISTANCE for any stall the ring can still replay, so the contrast is made in
    // units of the replay's own deficit (ReplayRampDeficit) rather than against that distance.
    private const int StallTicks = 80;

    // What a command replay cannot recover (character-gaits wave). The buffered inbox replays the
    // backlog frame by frame, so the server re-walks the same commands -- but a replay carries
    // commands, not velocity, and the server resumes from rest (it idled and braked during the
    // stall) while the client's prediction for the same frame was already at cruise. The server
    // therefore stays one ramp deficit behind the prediction for that frame:
    //   ramp deficit = cruise * ramp time - ramp distance = cruise^2 / (2 * acceleration)
    // Part of it is paid for in advance by the distance the server coasted while braking, and floor
    // friction costs a ramping body more than a cruising one. The deficit is used as the bound
    // because it is the structural quantity: the whole velocity gap, with no credit for the coast.
    // The residual is positional
    // and permanent in this harness (nothing applies the correction); in production the reconciler
    // smooths it away, which is why the assert below also requires it to stay a Smooth correction
    // and nowhere near the 3 m snap the latest-only inbox takes.
    private const float ReplayRampDeficit =
        PlayerSimulationTuning.DefaultRunSpeed * PlayerSimulationTuning.DefaultRunSpeed
        / (2f * PlayerSimulationTuning.DefaultGroundAcceleration);

    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    private enum InboxKind
    {
        Latest,
        Buffered,
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongStallThenBurst_ReplaysTheBurst_AndEndsWithinTheRampDeficit(bool walkerIsPrimary)
    {
        Vector3 facing = FindOpenFacing(walkerIsPrimary);
        // The walker sends WalkTicks + StallTicks frames, the last StallTicks of them delayed, and
        // then stops sending; both runs then repeat its last frame and idle the same way.
        int sentFrames = WalkTicks + StallTicks;
        int totalTicks = sentFrames + StallTicks + WorldSessionRuntime.MaxCommandRepeatTicks + 8;

        List<Sample> predicted = Run(InboxKind.Buffered, walkerIsPrimary, facing, totalTicks, sentFrames, stalled: false, keepSending: false);
        List<Sample> buffered = Run(InboxKind.Buffered, walkerIsPrimary, facing, totalTicks, sentFrames, stalled: true, keepSending: false);
        List<Sample> latest = Run(InboxKind.Latest, walkerIsPrimary, facing, totalTicks, sentFrames, stalled: true, keepSending: false);

        float bufferedError = MaxErrorFromResume(buffered, predicted, out ClientReconciliationKind bufferedKind, (ulong)sentFrames);
        float latestError = MaxErrorFromResume(latest, predicted, out ClientReconciliationKind latestKind, (ulong)sentFrames);
        float bufferedFinal = Horizontal(buffered[^1].Position - predicted[^1].Position);
        float latestFinal = Horizontal(latest[^1].Position - predicted[^1].Position);
        float walked = Horizontal(predicted[^1].Position - predicted[0].Position);
        output.WriteLine(
            $"walker {(walkerIsPrimary ? "primary" : "secondary")}, {StallTicks}-tick stall then {StallTicks}-frame burst, walked {walked:F2} m: "
            + $"max correction latest {latestError:F3} m ({latestKind}), buffered {bufferedError:F3} m ({bufferedKind}); "
            + $"final position error latest {latestFinal:F3} m, buffered {bufferedFinal:F3} m");

        float minimumWalk = 0.5f * sentFrames * SimulationClock.FixedDeltaSeconds * PlayerSimulationTuning.DefaultRunSpeed;
        Assert.True(walked > minimumWalk, $"the walker only walked {walked} m");
        Assert.True(latestError >= 4f * ReplayRampDeficit, $"latest error {latestError} m");

        // The replay keeps the seat within one ramp deficit (ReplayRampDeficit) of the prediction
        // and never snaps, against the latest-only inbox's full-stall snap above. The premise
        // "ends exactly where the unstalled run ends" died with the instant motor: a replay of
        // commands cannot replay the velocity the server braked away.
        Assert.True(bufferedError <= ReplayRampDeficit, $"buffered error {bufferedError} m");
        Assert.NotEqual(ClientReconciliationKind.Snap, bufferedKind);
        Assert.True(bufferedError < latestError / 4f, $"buffered {bufferedError} m vs latest {latestError} m");
        Assert.True(bufferedFinal <= ReplayRampDeficit, $"buffered final error {bufferedFinal} m");
    }

    // A client that keeps sending one frame per tick after the burst keeps the backlog topped up.
    // The replay holds the seat on its prediction until the elastic shed cuts the backlog's
    // latency, which costs the idle ticks of the stall; this records that trade-off.
    [Fact]
    public void LongStallThenBurst_WithTheClientStillSending_ShedsTheBacklogLatency()
    {
        Vector3 facing = FindOpenFacing(walkerIsPrimary: false);
        int sentFrames = WalkTicks + StallTicks;
        int totalTicks = sentFrames + 3 * SimulationClock.AuthoritativeTicksPerSecond;

        List<Sample> predicted = Run(InboxKind.Buffered, false, facing, totalTicks, sentFrames, stalled: false, keepSending: true);
        List<Sample> buffered = Run(InboxKind.Buffered, false, facing, totalTicks, sentFrames, stalled: true, keepSending: true);
        List<Sample> latest = Run(InboxKind.Latest, false, facing, totalTicks, sentFrames, stalled: true, keepSending: true);

        int resume = sentFrames;
        float bufferedReplayError = MaxError(buffered, predicted, resume, resume + BufferedPlayerCommandInbox.ShedWindowTicks - 2);
        float bufferedError = MaxErrorFromResume(buffered, predicted, out _);
        float latestError = MaxErrorFromResume(latest, predicted, out _);
        ulong finalLag = buffered[^1].Tick - buffered[^1].Ack;
        output.WriteLine(
            $"steady sender after the burst: latest max correction {latestError:F3} m; buffered {bufferedReplayError:F3} m "
            + $"during the replay window, {bufferedError:F3} m after the shed; final ack lag {finalLag} ticks");

        // Same residual as above: the replay holds the seat within one ramp deficit of its
        // prediction, not on it, because the server resumes from rest (see ReplayRampDeficit).
        Assert.True(bufferedReplayError <= ReplayRampDeficit, $"replay error {bufferedReplayError} m");
        Assert.True(bufferedReplayError < latestError / 4f, $"replay {bufferedReplayError} m vs latest {latestError} m");
        Assert.InRange(finalLag, 0UL, 2UL);
    }

    private static float MaxErrorFromResume(
        List<Sample> server,
        List<Sample> predicted,
        out ClientReconciliationKind worstKind,
        ulong stopAtAck = ulong.MaxValue)
    {
        int resume = WalkTicks + StallTicks;
        ClientPredictionReconciler reconciler = new();
        foreach (Sample sample in predicted)
        {
            reconciler.RecordPrediction(sample.Ack, sample.Position);
        }

        worstKind = ClientReconciliationKind.None;
        float max = 0f;
        for (int i = resume; i < server.Count; i++)
        {
            if (i > resume && server[i - 1].Ack >= stopAtAck)
            {
                // The walker stopped sending; the client predicts nothing past its last command.
                break;
            }

            float error = ErrorAt(server[i], predicted);
            if (error > max)
            {
                max = error;
                worstKind = reconciler.Reconcile(server[i].Ack, server[i].Position).Kind;
            }
        }

        return max;
    }

    private static float MaxError(List<Sample> server, List<Sample> predicted, int from, int to)
    {
        float max = 0f;
        for (int i = from; i < to; i++)
        {
            max = Math.Max(max, ErrorAt(server[i], predicted));
        }

        return max;
    }

    // The server's position against what the client predicted for the acknowledged command.
    private static float ErrorAt(Sample server, List<Sample> predicted)
    {
        Sample atAck = predicted[(int)server.Ack - 1];
        return Horizontal(server.Position - atAck.Position);
    }

    private static float Horizontal(Vector3 delta)
    {
        return new Vector2(delta.X, delta.Z).Length;
    }

    // Once per seat for the class (ARCH-1c): the probe is deterministic set-up.
    private static readonly ProbeCache<bool, Vector3> OpenFacings = new(ProbeOpenFacing);

    private static Vector3 FindOpenFacing(bool walkerIsPrimary) => OpenFacings.Get(walkerIsPrimary);

    private static Vector3 ProbeOpenFacing(bool walkerIsPrimary)
    {
        // The startup terrain blocks some directions; walk along the most open axis.
        int ticks = WalkTicks + StallTicks + 16;
        return new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }
            .OrderByDescending(direction =>
            {
                List<Sample> probe = Run(InboxKind.Buffered, walkerIsPrimary, direction, ticks, ticks, stalled: false, keepSending: false);
                return Horizontal(probe[^1].Position - probe[0].Position);
            })
            .First();
    }

    private readonly record struct Sample(ulong Tick, ulong Ack, Vector3 Position);

    // The walker produces frame N on tick N. Unstalled, it arrives that tick. Stalled, frames
    // WalkTicks+1 .. WalkTicks+StallTicks arrive together on the tick after the last of them. With
    // keepSending, the walker sends a walking frame every tick to the end; otherwise it stops
    // after sentFrames.
    private static List<Sample> Run(
        InboxKind kind,
        bool walkerIsPrimary,
        Vector3 facing,
        int totalTicks,
        int sentFrames,
        bool stalled,
        bool keepSending)
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
        BufferedPlayerCommandInbox buffered = new();
        LatestPlayerCommandInbox latest = new();
        IPlayerCommandInbox inbox = kind == InboxKind.Buffered ? buffered : latest;
        List<PlayerSessionId> sessions = [new PlayerSessionId(Steady), new PlayerSessionId(Walker)];
        List<PlayerCommandFrame> commands = [];
        List<Sample> samples = [];
        PlayerSessionId walker = new(Walker);
        ulong ack = 0;
        int stallStart = WalkTicks + 1;
        int stallEnd = WalkTicks + StallTicks;
        for (int tick = 1; tick <= totalTicks; tick++)
        {
            inbox.Enqueue(Command(Steady, (ulong)tick, 0f, facing));
            bool sends = tick <= sentFrames || keepSending;
            if (sends && (!stalled || tick < stallStart || tick > stallEnd))
            {
                if (stalled && tick == stallEnd + 1)
                {
                    // The delayed backlog arrives just before this tick's own frame.
                    for (int held = stallStart; held <= stallEnd; held++)
                    {
                        inbox.Enqueue(Command(Walker, (ulong)held, 1f, facing));
                    }
                }

                inbox.Enqueue(Command(Walker, (ulong)tick, 1f, facing));
            }
            else if (stalled && !sends && tick == stallEnd + 1)
            {
                for (int held = stallStart; held <= stallEnd; held++)
                {
                    inbox.Enqueue(Command(Walker, (ulong)held, 1f, facing));
                }
            }

            if (kind == InboxKind.Buffered)
            {
                buffered.DrainInto(sessions, commands);
            }
            else
            {
                latest.DrainInto(sessions, commands);
            }

            foreach (PlayerCommandFrame command in commands)
            {
                if (command.SessionId == walker)
                {
                    ack = command.Sequence;
                }
            }

            host.AdvanceTick((ulong)tick, SimulationClock.FixedDeltaSeconds, commands, Tuning, allowGameplaySimulation: false);
            samples.Add(new Sample((ulong)tick, ack, Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == Walker).Position));
        }

        return samples;
    }

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
