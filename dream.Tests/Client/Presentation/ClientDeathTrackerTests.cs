using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Presentation;

public sealed class ClientDeathTrackerTests
{
    private const ulong DeathTick = 1_000;
    private const int PerSecond = 64;

    [Fact]
    public void CountsDownFromFiveToZero_FromAFixedTick()
    {
        ClientDeathTracker tracker = new();
        tracker.Observe(isAlive: false, DeathTick);

        Assert.True(tracker.IsDead);
        Assert.Equal(5, tracker.SecondsRemaining(DeathTick));
        Assert.Equal(5, tracker.SecondsRemaining(DeathTick + 1));
        Assert.Equal(4, tracker.SecondsRemaining(DeathTick + PerSecond));
        Assert.Equal(3, tracker.SecondsRemaining(DeathTick + 2 * PerSecond));
        Assert.Equal(2, tracker.SecondsRemaining(DeathTick + 3 * PerSecond));
        Assert.Equal(1, tracker.SecondsRemaining(DeathTick + 4 * PerSecond));
        Assert.Equal(1, tracker.SecondsRemaining(DeathTick + 5 * PerSecond - 1));
        Assert.Equal(0, tracker.SecondsRemaining(DeathTick + 5 * PerSecond));
        Assert.Equal(0, tracker.SecondsRemaining(DeathTick + 500));
        Assert.True(tracker.IsDead, "still dead until the respawn snapshot arrives");
    }

    [Fact]
    public void TheWaitRunsFromTheFirstDeadSnapshot_NotFromLaterOnes()
    {
        ClientDeathTracker tracker = new();
        tracker.Observe(false, DeathTick);
        tracker.Observe(false, DeathTick + 128);

        Assert.Equal(3, tracker.SecondsRemaining(DeathTick + 128));
    }

    [Fact]
    public void AJoinWhileDead_ShowsTheOverlayAtOnce_WithAFreshWait()
    {
        ClientDeathTracker tracker = new();
        tracker.Observe(true, 10);
        tracker.Reset();
        tracker.Observe(false, 50_000);

        Assert.True(tracker.IsDead);
        Assert.Equal(5, tracker.SecondsRemaining(50_000));
    }

    [Fact]
    public void ARespawnSnapshot_ClearsIt()
    {
        ClientDeathTracker tracker = new();
        tracker.Observe(false, DeathTick);

        tracker.Observe(true, DeathTick + 320);

        Assert.False(tracker.IsDead);
        Assert.Equal(0, tracker.SecondsRemaining(DeathTick + 321));
    }

    [Fact]
    public void AStaleOldTickSnapshot_DoesNotReopenIt()
    {
        ClientDeathTracker tracker = new();
        tracker.Observe(false, DeathTick);
        tracker.Observe(true, DeathTick + 320);

        tracker.Observe(false, DeathTick + 10);

        Assert.False(tracker.IsDead);
    }

    [Fact]
    public void ASecondDeath_StartsANewWait()
    {
        ClientDeathTracker tracker = new();
        tracker.Observe(false, DeathTick);
        tracker.Observe(true, DeathTick + 320);
        tracker.Observe(false, DeathTick + 900);

        Assert.True(tracker.IsDead);
        Assert.Equal(5, tracker.SecondsRemaining(DeathTick + 900));
    }

    [Fact]
    public void NotDead_ReportsZero_AndAHostileTickNeverThrows()
    {
        ClientDeathTracker tracker = new();
        Assert.Equal(0, tracker.SecondsRemaining(123));

        tracker.Observe(false, ulong.MaxValue);
        Assert.Equal(0, tracker.SecondsRemaining(double.NaN));
        Assert.InRange(tracker.SecondsRemaining(double.NegativeInfinity), 0, 5);
        Assert.Equal(0, tracker.SecondsRemaining(double.PositiveInfinity));
    }

    [Fact]
    public void TheWaitMatchesTheServersFiveSeconds()
    {
        Assert.Equal(5 * 64, ClientDeathTracker.RespawnDelayTicks);
    }

    [Fact]
    public void RespawnLabel_ReadsAsTheSpecSays_AndAllocatesNothingOnARepeat()
    {
        HudStatusSnapshot waiting = new(0, 100, PlayerInteractionMode.Building, 1, 2, 10, 1f, 10, 1, 0, 100,
            IsDead: true, RespawnSeconds: 4);
        HudStatusSnapshot done = waiting with { RespawnSeconds = 0 };

        Assert.Equal("Respawning in 4 s", waiting.RespawnLabel);
        Assert.Equal("Respawning", done.RespawnLabel);
        Assert.Equal("You died", HudStatusSnapshot.DeathTitle);
        Assert.Same(waiting.RespawnLabel, waiting.RespawnLabel);
    }

    // The reconciler only re-compares on a new ack. Dead commands keep their sequence, so the ack
    // keeps advancing and the respawn teleport is compared and snapped, at any distance.
    [Theory]
    [InlineData(10f)]
    [InlineData(500f)]
    public void TheRespawnTeleport_SnapsThroughTheReconciler_WhenTheAckArrivesAfterIt(float distance)
    {
        ClientPredictionReconciler reconciler = new();
        Vector3 deathPosition = new(40f, 1f, 40f);
        Vector3 spawn = new(5f + distance, 1f, 5f);

        // The client predicts standing still at the death position for 40 ticks (dead: no input).
        for (ulong tick = 1; tick <= 40; tick++)
        {
            reconciler.RecordPrediction(tick, deathPosition, default);
        }

        // A snapshot before the respawn: the server agrees (ack 10 at the death position).
        Assert.Equal(ClientReconciliationKind.None, reconciler.Reconcile(10, deathPosition).Kind);
        // A snapshot after the respawn teleport that still repeats ack 10: nothing to compare yet.
        Assert.Equal(ClientReconciliationKind.None, reconciler.Reconcile(10, spawn).Kind);

        // The ack that includes the teleport arrives later and is compared against the prediction.
        ClientReconciliationResult result = reconciler.Reconcile(25, spawn);

        Assert.Equal(ClientReconciliationKind.Snap, result.Kind);
        Assert.Equal(spawn, result.SnapPosition);
    }
}
