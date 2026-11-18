using BulletSharp.Math;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;
using Xunit.Abstractions;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Client.Replication;

// Character-gaits fix loop 1, M1. ClientCorrectionApplier's snap branch used to leave the body's
// velocity at the zero SetWorldPosition writes. That was free under the old instant-velocity
// motor: the tick after the snap set the commanded speed outright. PlayerMotor now ramps from the
// body's own horizontal velocity, so a zeroed body spends v / GroundAcceleration getting back to
// speed while the server never stopped, and falls behind by
//   v^2 / (2a) at the cruise, and more at the sprint tier
// on every snap, and it also drops HorizontalSpeed to 0 for the length of the ramp, which replays
// the figure's idle -> walk -> run stride on every correction.
//
// The replayed snap position already carries the unacknowledged travel, so the pre-snap
// horizontal velocity is the state that belongs with it. These measure that it survives.
public class SnapVelocityHandoffTests(ITestOutputHelper output)
{
    private const float Cruise = PlayerSimulationTuning.DefaultRunSpeed;
    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    // Ticks measured after the snap: the whole ticks the ramp needs, v / a, so this is the window
    // in which a zeroed body is still recovering and the difference is at its widest: a body held
    // at cruise covers ticks x v / 64 over them, one restarting from rest covers
    // 0.5 x a x (ticks / 64)^2, less than half as far.
    private const int MeasuredTicks =
        (int)(Cruise / PlayerSimulationTuning.DefaultGroundAcceleration * SimulationClock.AuthoritativeTicksPerSecond);

    private const float HeldDistance =
        (float)MeasuredTicks / SimulationClock.AuthoritativeTicksPerSecond * Cruise;

    private static readonly float RestartedDistance =
        0.5f * PlayerSimulationTuning.DefaultGroundAcceleration
        * MathF.Pow((float)MeasuredTicks / SimulationClock.AuthoritativeTicksPerSecond, 2f);

    [Fact]
    public void Snap_KeepsTheHorizontalVelocity_SoTheRampDoesNotRestart()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ClientPredictionReconciler reconciler = new();
        Vector3 start = runtime.Player.GetWorldPosition();
        reconciler.RecordPrediction(3, start);
        reconciler.RecordPrediction(4, start + new Vector3(0.1f, 0f, 0f));
        runtime.Player.SetFacing(Vector3.UnitX);
        runtime.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(Cruise, 0f, 0f);

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            CreateLocalSnapshot(runtime, new Vector3(5f, 1.5f, 5f), acknowledgedSequence: 3),
            runtime.LocalPlayerSessionId.Value,
            reconciler);

        BulletSharp.Math.Vector3 afterSnap = runtime.Player.PhysicsBody.LinearVelocity;
        float speedAfterSnap = new Vector3(afterSnap.X, 0f, afterSnap.Z).Length;

        // Measured, not asserted into existence: how far the snapped body actually gets while a
        // zeroed one would be climbing its ramp again.
        Vector3 landing = runtime.Player.GetWorldPosition();
        for (ulong tick = 5; tick < 5 + MeasuredTicks; tick++)
        {
            runtime.AdvancePredictionTick(tick, SimulationClock.FixedDeltaSeconds, Command(tick, 1f, Vector3.UnitX), Tuning);
        }

        float travelled = (runtime.Player.GetWorldPosition() - landing).Xz.Length;
        output.WriteLine(
            $"speed right after the snap {speedAfterSnap:F3} m/s (cruise {Cruise}); "
            + $"travelled {travelled:F3} m over the next {MeasuredTicks} ticks "
            + $"(held at cruise {HeldDistance:F3} m, restarting from rest {RestartedDistance:F3} m)");

        // The snap keeps the horizontal velocity and still clears the vertical one.
        Assert.Equal(Cruise, speedAfterSnap, 3);
        Assert.Equal(0f, afterSnap.Y);

        // Over the ramp window the body stays on the cruising side of the two curves above. Anything
        // above their midpoint cannot be a body that restarted from rest.
        float midpoint = (HeldDistance + RestartedDistance) / 2f;
        Assert.True(HeldDistance > RestartedDistance);
        Assert.True(travelled > midpoint, $"only travelled {travelled} m: the ramp restarted");
    }

    // A snap whose replay was discarded is a teleport (or a path that ran into a solid cell): it
    // lands on the server position with none of the client's unacknowledged travel on it, so there
    // is no momentum that belongs with the landing and the body keeps none.
    [Fact]
    public void SnapWithADiscardedReplay_StillZeroesTheVelocity()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ClientPredictionReconciler reconciler = new(replayDiscardDistance: 0.5f);
        Vector3 start = runtime.Player.GetWorldPosition();
        reconciler.RecordPrediction(3, start);
        reconciler.RecordPrediction(4, start + new Vector3(0.1f, 0f, 0f));
        runtime.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(Cruise, 0f, 0f);

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            CreateLocalSnapshot(runtime, new Vector3(5f, 1.5f, 5f), acknowledgedSequence: 3),
            runtime.LocalPlayerSessionId.Value,
            reconciler);

        Assert.Equal(1, reconciler.DiscardedReplays);
        Assert.Equal(0f, runtime.Player.PhysicsBody.LinearVelocity.Length, 4);
    }

    private static PlayerCommandFrame Command(ulong sequence, float forward, Vector3 facing)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId("snap-velocity"),
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(forward, 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private static ServerSnapshotEnvelope CreateLocalSnapshot(ClientWorldSession runtime, Vector3 position, ulong acknowledgedSequence)
    {
        return new ServerSnapshotEnvelope(
            40,
            new Dictionary<string, ulong> { [runtime.LocalPlayerSessionId.Value] = acknowledgedSequence },
            [
                new ActorSnapshot(
                    runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    runtime.LocalPlayerSessionId.Value,
                    position,
                    Vector3.UnitX,
                    100,
                    100,
                    true)
            ],
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    // The snap moves the body from (9, 1.5, 9) to (5, 1.5, 5), 5.66 m: past the 3 m snap distance
    // and inside the 6 m replay discard distance, and the walk after it runs +X from x = 5.1 in a
    // 10-cell floor, which leaves it 4 m of lane.
    private static ClientWorldSession CreateRuntime()
    {
        return new ClientWorldSession(floorSize: 10, playerStartPosition: new Vector3(9f, 1.5f, 9f), new SceneEventHub());
    }
}
