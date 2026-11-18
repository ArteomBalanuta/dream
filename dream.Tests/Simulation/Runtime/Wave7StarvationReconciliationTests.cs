using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// Wave 7 tester (NETR-3 x client prediction). A client keeps predicting while its commands are
// stuck in the network; the server repeats the last command for MaxCommandRepeatTicks and then
// idles the seat. LatestPlayerCommandInbox keeps only the newest frame, so the backlog that
// arrives on resume moves the server one tick, not the whole stall. The acknowledged prediction is
// then ahead of the server by the idle ticks: a smooth correction for short stalls, a snap once
// the idle distance reaches ClientPredictionReconciler.DefaultSnapDistance.
public class Wave7StarvationReconciliationTests(ITestOutputHelper output)
{
    private const string Steady = "session-a";
    private const string Walker = "session-b";
    private const int WalkTicks = 2;
    private const int ResumeTicks = 2;

    // See StallPastTheSnapDistance_SnapsTheWalkerBack for the derivation.
    private const int StallTicks = (int)(
        (ClientPredictionReconciler.DefaultSnapDistance + 2f)
        / (PlayerSimulationTuning.DefaultRunSpeed / SimulationClock.AuthoritativeTicksPerSecond));

    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    [Fact]
    public void StallUpToTheRepeatWindow_ReconcilesWithoutAnyCorrection()
    {
        ClientReconciliationResult result = RunStall(WorldSessionRuntime.MaxCommandRepeatTicks, out float error);

        Assert.True(error < ClientPredictionReconciler.DefaultDeadbandDistance, $"error {error} m");
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
    }

    [Fact]
    public void TwentyTickStall_IsASmoothCorrection_OfAboutFourIdleTicksOfWalking()
    {
        ClientReconciliationResult result = RunStall(20, out float error);

        Assert.Equal(ClientReconciliationKind.Smooth, result.Kind);
        Assert.InRange(error, ClientPredictionReconciler.DefaultDeadbandDistance, 1f);
    }

    // Documents the NETR-3 trade-off rather than a defect: a walking client whose commands stall
    // long enough is pulled back by everything it predicted after the 16 repeated ticks.
    //
    // The snap threshold is a DISTANCE (DefaultSnapDistance), so the stall that crosses it depends
    // on the cruise speed: the client cruises the whole stall, the server walks WalkTicks +
    // MaxCommandRepeatTicks, brakes to rest and then stands still, so every stall tick past that
    // adds a cruise tick to the gap. StallTicks is therefore derived: the ticks the cruise needs to
    // cover the snap distance plus a margin that also pays for the server's own walk and braking.
    // What the test protects is "a long stall snaps", not a particular number of seconds.
    [Fact]
    public void StallPastTheSnapDistance_SnapsTheWalkerBack()
    {
        ClientReconciliationResult result = RunStall(StallTicks, out float error);

        output.WriteLine($"{StallTicks}-tick stall: {result.Kind}, error {error:F2} m");
        Assert.True(error >= ClientPredictionReconciler.DefaultSnapDistance, $"error {error} m");
        Assert.Equal(ClientReconciliationKind.Snap, result.Kind);
    }

    // The walker sends forward commands every tick. The control run (what the client predicts)
    // receives all of them; the stalled run receives none for stallTicks, then the newest one.
    private ClientReconciliationResult RunStall(int stallTicks, out float horizontalError)
    {
        int totalTicks = WalkTicks + stallTicks + ResumeTicks;
        // The startup terrain blocks some directions; walk along the most open axis.
        Vector3 facing = new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }
            .OrderByDescending(direction =>
            {
                List<(ulong Sequence, Vector3 Position)> probe = Run(totalTicks, int.MaxValue, 0, direction);
                return (probe[^1].Position - probe[0].Position).Xz.Length;
            })
            .First();
        List<(ulong Sequence, Vector3 Position)> predicted = Run(totalTicks, stallFrom: int.MaxValue, stallTicks: 0, facing);
        List<(ulong Sequence, Vector3 Position)> server = Run(totalTicks, stallFrom: WalkTicks + 1, stallTicks: stallTicks, facing);

        ClientPredictionReconciler reconciler = new();
        foreach ((ulong sequence, Vector3 position) in predicted)
        {
            reconciler.RecordPrediction(sequence, position);
        }

        // The snapshot after the resume tick acknowledges the newest command it merged.
        int resumeIndex = WalkTicks + stallTicks;
        (ulong ack, Vector3 serverPosition) = server[resumeIndex];
        Vector3 predictedAtAck = predicted[resumeIndex].Position;
        horizontalError = new Vector2(serverPosition.X - predictedAtAck.X, serverPosition.Z - predictedAtAck.Z).Length;
        output.WriteLine($"stall {stallTicks} ticks: ack {ack}, server {serverPosition}, predicted {predictedAtAck}, error {horizontalError:F3} m");
        return reconciler.Reconcile(ack, serverPosition);
    }

    private static List<(ulong Sequence, Vector3 Position)> Run(int totalTicks, int stallFrom, int stallTicks, Vector3 facing)
    {
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Steady, Steady);
        host.JoinSession(Walker, Walker);
        runtime.Enemies.Clear();
        List<(ulong, Vector3)> positions = [];
        ulong lastAccepted = 0;
        for (int tick = 1; tick <= totalTicks; tick++)
        {
            List<PlayerCommandFrame> commands = [Command(Steady, (ulong)tick, 0f, facing)];
            bool stalled = tick >= stallFrom && tick < stallFrom + stallTicks;
            if (!stalled)
            {
                // On resume the inbox holds only the newest frame of the backlog.
                commands.Add(Command(Walker, (ulong)tick, 1f, facing));
                lastAccepted = (ulong)tick;
            }

            host.AdvanceTick((ulong)tick, SimulationClock.FixedDeltaSeconds, commands, Tuning, allowGameplaySimulation: false);
            positions.Add((stalled ? (ulong)tick : lastAccepted, Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == Walker).Position));
        }

        return positions;
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
