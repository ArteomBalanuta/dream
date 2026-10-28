using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Presentation;

// NET-4: remote enemies interpolate on the server-tick timeline instead of teleporting once per
// snapshot. The presenter keeps one tick-keyed buffer per live enemy; the renderer reads it and
// EnemyActor.Position stays the latest authoritative position.
public class EnemyRenderInterpolationTests
{
    private const string LocalSessionId = "local-session";
    private const int WolfId = 2000;

    private readonly ITestOutputHelper _output;

    public EnemyRenderInterpolationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TwoSnapshotsFourTicksApart_SampledHalfway_GiveMidpoint()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(10, Wolf(WolfId, new Vector3(0f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(14, Wolf(WolfId, new Vector3(2f, 0f, -2f))));

        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 12d, out InterpolatedActorPose pose));
        Assert.Equal(1f, pose.Position.X, 4);
        Assert.Equal(0f, pose.Position.Y, 4);
        Assert.Equal(-1f, pose.Position.Z, 4);
    }

    [Fact]
    public void SameFrameBurstOfThreeSnapshots_InterpolatesAcrossEachTick()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(30, Wolf(WolfId, new Vector3(0f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(31, Wolf(WolfId, new Vector3(1f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(32, Wolf(WolfId, new Vector3(3f, 0f, 0f))));

        Assert.Equal(3, presenter.GetRemoteEnemySampleCount(WolfId));
        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 30.5d, out InterpolatedActorPose first));
        Assert.Equal(0.5f, first.Position.X, 4);
        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 31.5d, out InterpolatedActorPose second));
        Assert.Equal(2f, second.Position.X, 4);
        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 40d, out InterpolatedActorPose held));
        Assert.Equal(3f, held.Position.X, 4);
    }

    [Fact]
    public void Facing_InterpolatesAndNormalizes_AndDegenerateFacingKeepsLast()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(1, Wolf(WolfId, Vector3.Zero, facing: Vector3.UnitX)));
        presenter.ApplySnapshot(Snapshot(3, Wolf(WolfId, Vector3.Zero, facing: Vector3.UnitZ)));
        presenter.ApplySnapshot(Snapshot(5, Wolf(WolfId, Vector3.Zero, facing: Vector3.Zero)));

        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 2d, out InterpolatedActorPose middle));
        float diagonal = MathF.Sqrt(0.5f);
        Assert.Equal(diagonal, middle.Facing.X, 4);
        Assert.Equal(diagonal, middle.Facing.Z, 4);

        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 5d, out InterpolatedActorPose latest));
        Assert.Equal(1f, latest.Facing.Z, 4);
    }

    [Fact]
    public void RemovedEnemy_PrunesItsBuffer()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(1, Wolf(WolfId, Vector3.Zero), Wolf(WolfId + 1, Vector3.One)));
        presenter.ApplySnapshot(Snapshot(2, Wolf(WolfId, Vector3.UnitX), Wolf(WolfId + 1, Vector3.One)));
        Assert.Equal(2, presenter.RemoteEnemyBufferCount);

        presenter.ApplySnapshot(Snapshot(3, Wolf(WolfId + 1, Vector3.One)));

        Assert.Equal(1, presenter.RemoteEnemyBufferCount);
        Assert.Equal(0, presenter.GetRemoteEnemySampleCount(WolfId));
        Assert.False(presenter.TryGetEnemyRenderPose(WolfId, 2.5d, out _));
        Assert.True(presenter.TryGetEnemyRenderPose(WolfId + 1, 2.5d, out _));
    }

    [Fact]
    public void DeadEnemy_StopsInterpolating()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(1, Wolf(WolfId, Vector3.Zero)));
        presenter.ApplySnapshot(Snapshot(2, Wolf(WolfId, Vector3.UnitX, health: 0, isAlive: false)));

        Assert.Equal(0, presenter.RemoteEnemyBufferCount);
        Assert.False(presenter.TryGetEnemyRenderPose(WolfId, 1.5d, out _));
    }

    [Fact]
    public void RespawnUnderSameIdAfterDeath_ResetsAndDoesNotSlide()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(10, Wolf(WolfId, new Vector3(0f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(11, Wolf(WolfId, new Vector3(0.1f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(12, Wolf(WolfId, new Vector3(0.1f, 0f, 0f), health: 0, isAlive: false)));
        presenter.ApplySnapshot(Snapshot(13, Wolf(WolfId, new Vector3(1f, 0f, 1f))));

        Assert.Equal(1, presenter.GetRemoteEnemySampleCount(WolfId));
        // A render tick still behind the respawn shows the enemy at its respawn point.
        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 10.5d, out InterpolatedActorPose pose));
        Assert.Equal(new Vector3(1f, 0f, 1f), pose.Position);
    }

    [Fact]
    public void RespawnWithDeathSnapshotCoalescedAway_LargeJumpResetsAndDoesNotSlide()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(10, Wolf(WolfId, new Vector3(0f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(11, Wolf(WolfId, new Vector3(0.1f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(13, Wolf(WolfId, new Vector3(20f, 0f, 0f))));

        Assert.Equal(1, presenter.GetRemoteEnemySampleCount(WolfId));
        Assert.True(presenter.TryGetEnemyRenderPose(WolfId, 12d, out InterpolatedActorPose pose));
        Assert.Equal(20f, pose.Position.X, 4);
    }

    [Fact]
    public void NormalStep_DoesNotReset()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(10, Wolf(WolfId, new Vector3(0f, 0f, 0f))));
        presenter.ApplySnapshot(Snapshot(14, Wolf(WolfId, new Vector3(1.5f, 0f, 0f))));

        Assert.Equal(2, presenter.GetRemoteEnemySampleCount(WolfId));
    }

    [Fact]
    public void JoinAccepted_ClearsEnemyBuffers()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        presenter.ApplySnapshot(Snapshot(500, Wolf(WolfId, Vector3.Zero)));

        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            LocalSessionId,
            1,
            0,
            InitialWorld: null,
            Roster: []));

        Assert.Equal(0, presenter.RemoteEnemyBufferCount);
    }

    [Fact]
    public void RenderPose_IsInterpolated_WhileActorPositionStaysAuthoritative()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        EnemyRegistry registry = new();
        ServerSnapshotEnvelope first = Snapshot(10, Wolf(WolfId, new Vector3(0f, 0f, 0f), facing: Vector3.UnitX));
        ServerSnapshotEnvelope second = Snapshot(14, Wolf(WolfId, new Vector3(2f, 0f, 0f), facing: Vector3.UnitX));
        foreach (ServerSnapshotEnvelope snapshot in new[] { first, second })
        {
            RemoteEnemyStateApplier.ApplyActorSnapshots(registry, snapshot.Actors);
            presenter.ApplySnapshot(snapshot);
        }

        Assert.True(registry.TryGetEnemyByActorId(WolfId, out EnemyActor? enemy));
        InterpolatedActorPose pose = EnemyRenderer.ResolveRenderPose(enemy!, presenter, 12d);

        Assert.Equal(1f, pose.Position.X, 4);
        Assert.Equal(1f, pose.Facing.X, 4);
        Assert.Equal(new Vector3(2f, 0f, 0f), enemy!.Position);
    }

    [Fact]
    public void RenderPose_WithoutBuffer_FallsBackToAuthoritativePose()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        EnemyRegistry registry = new();
        RemoteEnemyStateApplier.ApplyActorSnapshots(registry, [Wolf(WolfId, new Vector3(3f, 0f, 4f), facing: Vector3.UnitX)]);
        Assert.True(registry.TryGetEnemyByActorId(WolfId, out EnemyActor? enemy));

        InterpolatedActorPose withoutSource = EnemyRenderer.ResolveRenderPose(enemy!, null, 5d);
        InterpolatedActorPose withoutBuffer = EnemyRenderer.ResolveRenderPose(enemy!, presenter, 5d);

        Assert.Equal(new Vector3(3f, 0f, 4f), withoutSource.Position);
        Assert.Equal(enemy!.Forward, withoutSource.Facing);
        Assert.Equal(withoutSource, withoutBuffer);
    }

    // Before NET-4 the presenter skipped enemies, so ApplySnapshot with two enemies measured 0 B
    // (ClientScenePresenterAllocationTests) and the renderer read EnemyActor.Position (0 B).
    [Fact]
    public void ApplySnapshot_MovingEnemies_SteadyState_AllocatesNothing()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        ServerSnapshotEnvelope a = Snapshot(1, LocalPlayer(), Wolf(WolfId, new Vector3(0f, 0f, 0f)), Wolf(WolfId + 1, new Vector3(5f, 0f, 0f)));
        ServerSnapshotEnvelope b = Snapshot(1, LocalPlayer(), Wolf(WolfId, new Vector3(0.5f, 0f, 0f)), Wolf(WolfId + 1, new Vector3(5.5f, 0f, 0f)));
        ulong tick = 1;
        for (; tick <= 40; tick++)
        {
            presenter.ApplySnapshot((tick % 2 == 0 ? a : b) with { ServerTick = tick });
        }

        long bytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                presenter.ApplySnapshot((tick % 2 == 0 ? a : b) with { ServerTick = tick });
                tick++;
            });

        _output.WriteLine($"ApplySnapshot with 2 moving enemies: {bytes} B per call");
        Assert.Equal(2, presenter.RemoteEnemyBufferCount);
    }

    [Fact]
    public void ApplySnapshot_EnemyDespawnAndRespawnChurn_SteadyState_AllocatesNothing()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        ServerSnapshotEnvelope both = Snapshot(1, Wolf(WolfId, new Vector3(0f, 0f, 0f)), Wolf(WolfId + 1, new Vector3(5f, 0f, 0f)));
        ServerSnapshotEnvelope one = Snapshot(1, Wolf(WolfId + 1, new Vector3(5f, 0f, 0f)));
        ulong tick = 1;
        for (; tick <= 40; tick++)
        {
            presenter.ApplySnapshot((tick % 2 == 0 ? both : one) with { ServerTick = tick });
        }

        long bytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                presenter.ApplySnapshot((tick % 2 == 0 ? both : one) with { ServerTick = tick });
                tick++;
            });

        _output.WriteLine($"ApplySnapshot with enemy churn: {bytes} B per call");
    }

    [Fact]
    public void RenderPoseLookup_PerFrame_AllocatesNothing()
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        EnemyRegistry registry = new();
        for (ulong tick = 1; tick <= 8; tick++)
        {
            ServerSnapshotEnvelope snapshot = Snapshot(tick, Wolf(WolfId, new Vector3(tick * 0.25f, 0f, 0f)));
            RemoteEnemyStateApplier.ApplyActorSnapshots(registry, snapshot.Actors);
            presenter.ApplySnapshot(snapshot);
        }

        Assert.True(registry.TryGetEnemyByActorId(WolfId, out EnemyActor? enemy));
        IEnemyRenderPoseSource source = presenter;
        double renderTick = 5.5d;

        long bytes = AllocationAssert.AtMost(
            0,
            () => EnemyRenderer.ResolveRenderPose(enemy!, source, renderTick));

        _output.WriteLine($"ResolveRenderPose: {bytes} B per call");
        Assert.Equal(1.375f, EnemyRenderer.ResolveRenderPose(enemy!, source, renderTick).Position.X, 4);
    }

    private static ServerSnapshotEnvelope Snapshot(ulong tick, params ActorSnapshot[] actors)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private static ActorSnapshot Wolf(
        int actorId,
        Vector3 position,
        Vector3? facing = null,
        int health = 50,
        bool isAlive = true)
    {
        return new ActorSnapshot(
            actorId,
            CreatureKind.Enemy,
            null,
            position,
            facing ?? -Vector3.UnitZ,
            health,
            50,
            isAlive,
            DisplayName: "Wolf");
    }

    private static ActorSnapshot LocalPlayer()
    {
        return new ActorSnapshot(
            1,
            CreatureKind.Player,
            LocalSessionId,
            new Vector3(1f, 1.5f, 1f),
            -Vector3.UnitZ,
            100,
            100,
            true,
            DisplayName: "Local");
    }
}
